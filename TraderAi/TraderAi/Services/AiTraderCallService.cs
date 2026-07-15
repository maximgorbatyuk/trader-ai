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

public sealed record AiDecisionQualitySummary(
    int CallAttempts,
    int CompletedCalls,
    int InvalidJsonCalls,
    int OtherFailedCalls,
    double CallCompletionRate,
    int ProposedOrders,
    int AppliedOrders,
    int RejectedOrders,
    double ProposalAcceptanceRate,
    decimal ExecutedBuyNotional);

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

    // Rehydrates a stored decision to reapply a deferred plan. Uses the same options the decision was stored with.
    public AiTradeDecision? DeserializeDecision(string? decisionJson)
        => string.IsNullOrWhiteSpace(decisionJson)
            ? null
            : JsonSerializer.Deserialize<AiTradeDecision>(decisionJson, StoredJsonOptions);

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

            // A deferred plan is applied a day after it completed; recording its application settles it to Completed.
            // For the ordinary same-cycle path the status is already Completed, so this is a no-op there.
            stored.Status = AiTraderCallStatus.Completed;
            await dbContext.SaveChangesAsync();
        });
    }

    // Defers an end-of-day planning call: its stored decision is applied at the opening cycle of the target day.
    public async Task MarkPendingNextDayAsync(long callId, int targetDayNumber)
    {
        await WithLockAsync(async () =>
        {
            var stored = await dbContext.AiTraderCalls.FirstOrDefaultAsync(candidate => candidate.Id == callId);
            if (stored is null)
            {
                return;
            }

            stored.Status = AiTraderCallStatus.PendingNextDay;
            stored.NextDayTargetDayNumber = targetDayNumber;
            await dbContext.SaveChangesAsync();
        });
    }

    // A deferred plan whose target day passed without opening is abandoned so it never applies late.
    public async Task AbandonPendingNextDayAsync(long callId, string reason)
    {
        await WithLockAsync(async () =>
        {
            var stored = await dbContext.AiTraderCalls.FirstOrDefaultAsync(candidate => candidate.Id == callId);
            if (stored is null)
            {
                return;
            }

            stored.Status = AiTraderCallStatus.Abandoned;
            stored.Error = Truncate(reason, 2000);
            await dbContext.SaveChangesAsync();
        });
    }

    // Deferred plans that are due (target day opened) or stale (target day already passed) for one participant,
    // newest first, so the coordinator can apply the due one and abandon the rest.
    public Task<List<AiTraderCall>> GetDuePendingNextDayCallsAsync(int participantId, int currentDayNumber)
        => dbContext.AiTraderCalls
            .Where(call => call.ParticipantId == participantId
                && call.Status == AiTraderCallStatus.PendingNextDay
                && call.NextDayTargetDayNumber != null
                && call.NextDayTargetDayNumber <= currentDayNumber)
            .OrderByDescending(call => call.Id)
            .ToListAsync();

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

    // Read-only decision-quality summary for one AI trader: provider-call reliability (JSON completion), order-proposal
    // validity (acceptance), and capital actually deployed. Executed notional is realized buy trades rather than order
    // count, because order count overstates useful activity when few orders fill.
    public async Task<AiDecisionQualitySummary> GetDecisionQualityAsync(int participantId)
    {
        var statusCounts = await dbContext.AiTraderCalls
            .Where(call => call.ParticipantId == participantId)
            .GroupBy(call => call.Status)
            .Select(group => new
            {
                Status = group.Key,
                Count = group.Count(),
                Applied = group.Sum(call => call.AppliedOrders),
                Rejected = group.Sum(call => call.RejectedOrders),
            })
            .ToListAsync();

        int Count(params AiTraderCallStatus[] statuses)
            => statusCounts.Where(entry => statuses.Contains(entry.Status)).Sum(entry => entry.Count);

        var completed = Count(AiTraderCallStatus.Completed);
        var invalidJson = Count(AiTraderCallStatus.InvalidJson);
        var otherFailed = Count(AiTraderCallStatus.HttpError, AiTraderCallStatus.TimedOut);

        // A call is an attempt only once it has a terminal provider verdict; in-flight, deferred, operator-cancelled,
        // and restart-abandoned calls are not reliability signals.
        var attempts = completed + invalidJson + otherFailed;

        var applied = statusCounts.Sum(entry => entry.Applied);
        var rejected = statusCounts.Sum(entry => entry.Rejected);
        var proposed = applied + rejected;

        var executedBuyNotional = await dbContext.ShareTransactions
            .Where(trade => trade.BuyerId == participantId)
            .SumAsync(trade => (decimal?)trade.TotalCost) ?? 0m;

        return new AiDecisionQualitySummary(
            attempts,
            completed,
            invalidJson,
            otherFailed,
            attempts == 0 ? 0d : (double)completed / attempts,
            proposed,
            applied,
            rejected,
            proposed == 0 ? 0d : (double)applied / proposed,
            executedBuyNotional);
    }

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
