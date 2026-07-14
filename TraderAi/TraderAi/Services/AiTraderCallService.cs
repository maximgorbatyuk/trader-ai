using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record AiTraderCallDescriptor(
    int ParticipantId,
    string ParticipantName,
    string ProviderId,
    string ProviderLabel,
    string Model,
    int ConfigurationRevision,
    int SnapshotCycleId,
    int SnapshotCycleNumber,
    string PromptHash,
    string RequestJson);

public sealed record AiTraderCallExecution(long CallId, AiTraderCallStatus Status, AiTradeDecision? Decision);

public sealed record AiTraderCallSummary(
    long Id,
    string ProviderId,
    string ProviderLabel,
    string Model,
    string Status,
    int SnapshotCycleNumber,
    string? Summary,
    int AppliedOrders,
    int RejectedOrders,
    long? DurationMilliseconds,
    DateTime RequestedAt,
    DateTime? RespondedAt,
    DateTime? AppliedAt);

public sealed record AiTraderCallPage(IReadOnlyList<AiTraderCallSummary> Items, int Total, int Page, int PageSize);

// Writes and reads the AI-call audit log with short, serialized writes under the market lock. A Pending row is
// always saved before the provider is contacted, so a failed insert stops the call and a crash leaves a Pending
// row that startup recovery marks Abandoned. Parsed decisions and application results are stored with the same
// camel-case JSON the API uses; no key or Authorization value is ever written here.
public sealed class AiTraderCallService(AppDbContext dbContext, MarketCycleLock cycleLock)
{
    public const int MaxPageSize = 20;

    private static readonly JsonSerializerOptions StoredJsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<AiTraderCallExecution> ExecuteAsync(
        AiTraderCallDescriptor descriptor,
        int maxOrders,
        Func<CancellationToken, Task<AiProviderResponse>> send,
        CancellationToken cancellationToken)
    {
        var call = new AiTraderCall
        {
            ParticipantId = descriptor.ParticipantId,
            ParticipantName = descriptor.ParticipantName,
            ProviderId = descriptor.ProviderId,
            ProviderLabel = descriptor.ProviderLabel,
            Model = descriptor.Model,
            ConfigurationRevision = descriptor.ConfigurationRevision,
            SnapshotCycleId = descriptor.SnapshotCycleId,
            SnapshotCycleNumber = descriptor.SnapshotCycleNumber,
            PromptHash = descriptor.PromptHash,
            RequestJson = descriptor.RequestJson,
            Status = AiTraderCallStatus.Pending,
            RequestedAt = DateTime.UtcNow,
        };

        await WithLockAsync(async () =>
        {
            dbContext.AiTraderCalls.Add(call);
            await dbContext.SaveChangesAsync();
        });

        var stopwatch = Stopwatch.StartNew();
        var response = await send(cancellationToken);
        stopwatch.Stop();

        var status = MapStatus(response.Outcome);
        AiTradeDecision? decision = null;
        string? decisionJson = null;
        string? summary = null;
        var error = response.Error;

        if (response.Outcome == AiProviderCallOutcome.Success && response.AssistantContent is { } content)
        {
            if (AiDecisionJson.TryParse(content, maxOrders, out decision, out var parseError))
            {
                status = AiTraderCallStatus.Completed;
                decisionJson = JsonSerializer.Serialize(decision, StoredJsonOptions);
                summary = decision!.Summary;
            }
            else if (content.Trim() is { Length: > 0 } prose && !prose.Contains('{'))
            {
                // A reply that carries no JSON object at all is the model reasoning in prose and placing no orders,
                // so it is recorded as a completed no-order decision with that reasoning kept as the summary rather
                // than surfaced as a JSON parse error.
                decision = new AiTradeDecision(prose, []);
                status = AiTraderCallStatus.Completed;
                decisionJson = JsonSerializer.Serialize(decision, StoredJsonOptions);
                summary = decision.Summary;
            }
            else
            {
                status = AiTraderCallStatus.InvalidJson;
                decision = null;
                error = parseError;
            }
        }

        await WithLockAsync(async () =>
        {
            var stored = await dbContext.AiTraderCalls.FirstAsync(candidate => candidate.Id == call.Id);
            stored.ResponseBody = response.RawBody;
            stored.DecisionJson = decisionJson;
            stored.Summary = Truncate(summary, 1000);
            stored.Status = status;
            stored.HttpStatusCode = response.HttpStatusCode;
            stored.Error = Truncate(error, 2000);
            stored.PromptTokens = response.PromptTokens;
            stored.CompletionTokens = response.CompletionTokens;
            stored.TotalTokens = response.TotalTokens;
            stored.RespondedAt = DateTime.UtcNow;
            stored.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
            await dbContext.SaveChangesAsync();
        });

        return new AiTraderCallExecution(call.Id, status, decision);
    }

    public string SerializeApplicationResult<T>(T applicationResult)
        => JsonSerializer.Serialize(applicationResult, StoredJsonOptions);

    public async Task RecordApplicationAsync(
        long callId,
        string applicationResultJson,
        int appliedOrders,
        int rejectedOrders)
    {
        await WithLockAsync(async () =>
        {
            var stored = await dbContext.AiTraderCalls.FirstOrDefaultAsync(candidate => candidate.Id == callId);
            if (stored is null)
            {
                return;
            }

            stored.ApplicationResultJson = applicationResultJson;
            stored.AppliedOrders = appliedOrders;
            stored.RejectedOrders = rejectedOrders;
            stored.AppliedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
        });
    }

    // A row left Pending across a restart is orphaned: no in-flight call can still be tracking it.
    public async Task<int> AbandonStalePendingCallsAsync()
    {
        return await WithLockAsync(async () =>
        {
            var stale = await dbContext.AiTraderCalls
                .Where(call => call.Status == AiTraderCallStatus.Pending)
                .ToListAsync();
            foreach (var call in stale)
            {
                call.Status = AiTraderCallStatus.Abandoned;
                call.Error ??= "Abandoned because the application restarted before the call completed.";
            }

            if (stale.Count > 0)
            {
                await dbContext.SaveChangesAsync();
            }

            return stale.Count;
        });
    }

    public async Task<AiTraderCallPage> GetPageAsync(int participantId, int page, int pageSize)
    {
        var normalizedPage = Math.Max(1, page);
        var normalizedPageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = dbContext.AiTraderCalls.Where(call => call.ParticipantId == participantId);
        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(call => call.Id)
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .Select(call => new AiTraderCallSummary(
                call.Id,
                call.ProviderId,
                call.ProviderLabel,
                call.Model,
                call.Status.ToString(),
                call.SnapshotCycleNumber,
                call.Summary,
                call.AppliedOrders,
                call.RejectedOrders,
                call.DurationMilliseconds,
                call.RequestedAt,
                call.RespondedAt,
                call.AppliedAt))
            .ToListAsync();

        return new AiTraderCallPage(items, total, normalizedPage, normalizedPageSize);
    }

    public Task<AiTraderCall?> GetCallAsync(int participantId, long callId)
        => dbContext.AiTraderCalls
            .FirstOrDefaultAsync(call => call.Id == callId && call.ParticipantId == participantId);

    private static AiTraderCallStatus MapStatus(AiProviderCallOutcome outcome) => outcome switch
    {
        AiProviderCallOutcome.Success => AiTraderCallStatus.Completed,
        AiProviderCallOutcome.HttpError => AiTraderCallStatus.HttpError,
        AiProviderCallOutcome.MalformedResponse => AiTraderCallStatus.InvalidJson,
        AiProviderCallOutcome.TimedOut => AiTraderCallStatus.TimedOut,
        AiProviderCallOutcome.Cancelled => AiTraderCallStatus.Cancelled,
        _ => AiTraderCallStatus.HttpError,
    };

    private static string? Truncate(string? value, int maxLength)
        => value is null || value.Length <= maxLength ? value : value[..maxLength];

    private async Task WithLockAsync(Func<Task> action)
    {
        await cycleLock.Semaphore.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            cycleLock.Semaphore.Release();
        }
    }

    private async Task<T> WithLockAsync<T>(Func<Task<T>> action)
    {
        await cycleLock.Semaphore.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            cycleLock.Semaphore.Release();
        }
    }
}
