using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Runs provider inference beside the market loop. Each eligible AI trader has at most one call in flight; calls
// run outside the market lock and never delay a cycle. A global semaphore caps concurrency. The provider request
// is prepared credential-free and its audit row is written before the key is read and the call is sent, so a
// crash leaves a Pending row that startup recovery abandons. A stale configuration revision cannot apply.
public sealed class AiTraderCoordinator(
    IServiceScopeFactory scopeFactory,
    AiProviderCatalog catalog,
    AiTraderRuntimeState runtimeState,
    IOptions<AiTradingOptions> options,
    TimeProvider timeProvider,
    ILogger<AiTraderCoordinator> logger) : BackgroundService
{
    private readonly AiTradingOptions settings = options.Value;
    private readonly SemaphoreSlim concurrency = new(
        Math.Max(1, options.Value.MaxConcurrentRequests),
        Math.Max(1, options.Value.MaxConcurrentRequests));
    private readonly ConcurrentDictionary<int, Task> inFlight = new();
    private readonly ConcurrentDictionary<int, ParticipantSchedule> schedules = new();

    public IReadOnlyCollection<int> InFlightParticipants => inFlight.Keys.ToArray();

    public Task? InFlightTaskFor(int participantId) => inFlight.GetValueOrDefault(participantId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            return;
        }

        await AbandonStalePendingCallsAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1, settings.ScanIntervalMilliseconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ScanAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task AbandonStalePendingCallsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var callService = scope.ServiceProvider.GetRequiredService<AiTraderCallService>();
        await callService.AbandonStalePendingCallsAsync();
    }

    // One scan pass: start a call for every eligible trader that has no call in flight, whose retry window has
    // elapsed, and for whom a newer cycle exists than the one last processed.
    public async Task ScanAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TradingClockService>();

        var market = await dbContext.Markets.FirstOrDefaultAsync(stoppingToken);
        if (market is not { Status: MarketStatus.Running, CurrentCycleId: not null })
        {
            return;
        }

        if (!await clock.IsTradingAsync(market))
        {
            return;
        }

        var currentCycle = await dbContext.MarketCycles
            .FirstOrDefaultAsync(cycle => cycle.Id == market.CurrentCycleId, stoppingToken);
        if (currentCycle is null)
        {
            return;
        }

        var eligible = await (
            from configuration in dbContext.AiTraderConfigurations
            join participant in dbContext.Participants on configuration.ParticipantId equals participant.Id
            where participant.IsActive && !participant.IsBankrupt && participant.Type == ParticipantType.AIAgent
            select configuration.ParticipantId).ToListAsync(stoppingToken);

        var now = timeProvider.GetUtcNow();
        foreach (var participantId in eligible)
        {
            if (inFlight.ContainsKey(participantId))
            {
                continue;
            }

            if (schedules.TryGetValue(participantId, out var schedule))
            {
                if (schedule.NextRetryAt is { } retryAt && now < retryAt)
                {
                    continue;
                }

                if (schedule.NextRetryAt is null && schedule.LastCycleNumber == currentCycle.CycleNumber)
                {
                    continue;
                }
            }

            StartProcessing(participantId, currentCycle.CycleNumber, stoppingToken);
        }
    }

    private void StartProcessing(int participantId, int cycleNumber, CancellationToken stoppingToken)
    {
        var task = Task.Run(async () =>
        {
            await concurrency.WaitAsync(stoppingToken);
            try
            {
                await ProcessParticipantAsync(participantId, cycleNumber, stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AI trader processing failed for participant {ParticipantId}.", participantId);
            }
            finally
            {
                concurrency.Release();
                inFlight.TryRemove(participantId, out _);
            }
        }, stoppingToken);

        inFlight[participantId] = task;
    }

    public async Task<AiTraderCallStatus> ProcessParticipantAsync(
        int participantId,
        int cycleNumber,
        CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<AppDbContext>();

        var configuration = await dbContext.AiTraderConfigurations
            .FirstOrDefaultAsync(candidate => candidate.ParticipantId == participantId, stoppingToken);
        var participant = await dbContext.Participants
            .FirstOrDefaultAsync(candidate => candidate.Id == participantId, stoppingToken);
        if (configuration is null
            || participant is not { IsActive: true, IsBankrupt: false, Type: ParticipantType.AIAgent })
        {
            return AiTraderCallStatus.Abandoned;
        }

        var provider = catalog.Find(configuration.ProviderId);
        if (provider is null)
        {
            SetError(participantId, "The configured provider is no longer available.", null);
            return AiTraderCallStatus.Abandoned;
        }

        var snapshot = await services.GetRequiredService<AiMarketSnapshotBuilder>().BuildAsync(participantId);
        if (snapshot is null)
        {
            return AiTraderCallStatus.Abandoned;
        }

        var market = await dbContext.Markets.FirstOrDefaultAsync(stoppingToken);
        var prompt = services.GetRequiredService<AiTradingPromptBuilder>().Build(snapshot);
        var client = services.GetRequiredService<IAiProviderClient>();
        var prepared = client.Prepare(provider, configuration.Model, prompt.SystemMessage, prompt.UserMessage);

        var descriptor = new AiTraderCallDescriptor(
            participantId,
            participant.Name,
            configuration.ProviderId,
            provider.Label,
            configuration.Model,
            configuration.Revision,
            market?.CurrentCycleId ?? 0,
            snapshot.Market.CycleNumber,
            prompt.SystemMessageHash,
            prepared.RequestJson);

        var callService = services.GetRequiredService<AiTraderCallService>();
        var apiKey = configuration.ApiKey;

        runtimeState.Set(participantId, new AiTraderRuntimeSnapshot(
            AiTraderRuntimeStatus.Thinking, "Thinking", null, snapshot.Market.CycleNumber,
            timeProvider.GetUtcNow().UtcDateTime, null, null));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken, runtimeState.BeginCall(participantId));

        AiProviderResponse? response = null;
        var execution = await callService.ExecuteAsync(
            descriptor,
            settings.MaxOrdersPerDecision,
            async token =>
            {
                response = await client.SendAsync(prepared, apiKey, token);
                return response;
            },
            linked.Token);

        if (execution.Status == AiTraderCallStatus.Completed && execution.Decision is not null)
        {
            runtimeState.Set(participantId, new AiTraderRuntimeSnapshot(
                AiTraderRuntimeStatus.Applying, "Applying orders", execution.CallId, snapshot.Market.CycleNumber,
                timeProvider.GetUtcNow().UtcDateTime, null, null));

            // Apply in a fresh scope so the revision/eligibility guard and cycle tag read committed database
            // state rather than the entities this scope tracked before the multi-second provider call. Otherwise
            // an edit that bumped the revision mid-call would be masked by the stale tracked copy.
            AiDecisionApplicationResult application;
            using (var applyScope = scopeFactory.CreateScope())
            {
                application = await applyScope.ServiceProvider.GetRequiredService<MarketService>()
                    .ApplyAiDecisionAsync(participantId, configuration.Revision, execution.Decision);
            }

            var applied = application.Orders.Count(order => order.Applied);
            var rejected = application.Orders.Length - applied;
            await callService.RecordApplicationAsync(
                execution.CallId, callService.SerializeApplicationResult(application), applied, rejected);

            schedules[participantId] = new ParticipantSchedule { LastCycleNumber = cycleNumber };
            runtimeState.Set(participantId, new AiTraderRuntimeSnapshot(
                AiTraderRuntimeStatus.Waiting, $"Applied {applied} order(s), rejected {rejected}.",
                execution.CallId, snapshot.Market.CycleNumber, null, timeProvider.GetUtcNow().UtcDateTime, null));
            return execution.Status;
        }

        HandleFailure(participantId, cycleNumber, execution, response);
        return execution.Status;
    }

    private void HandleFailure(int participantId, int cycleNumber, AiTraderCallExecution execution, AiProviderResponse? response)
    {
        var now = timeProvider.GetUtcNow();
        var schedule = new ParticipantSchedule();

        switch (execution.Status)
        {
            case AiTraderCallStatus.HttpError when response?.HttpStatusCode is 401 or 403:
                schedule.NextRetryAt = now.AddSeconds(settings.AuthErrorRetrySeconds);
                SetError(participantId, "Authentication failed; check the API key.", schedule.NextRetryAt);
                break;
            case AiTraderCallStatus.HttpError:
            case AiTraderCallStatus.TimedOut:
                schedule.Attempts = NextAttempts(participantId);
                schedule.NextRetryAt = ComputeRetry(now, schedule.Attempts, response?.RetryAfter);
                SetError(participantId, "Provider call failed; will retry.", schedule.NextRetryAt);
                break;
            case AiTraderCallStatus.Cancelled:
                // A configuration edit cancelled the call; allow a fresh attempt immediately.
                schedules.TryRemove(participantId, out _);
                runtimeState.Set(participantId, AiTraderRuntimeSnapshot.Idle);
                return;
            default:
                // Invalid or malformed responses are not retried within the same cycle; a new cycle tries again.
                schedule.LastCycleNumber = cycleNumber;
                SetError(participantId, "The provider response could not be used.", null);
                break;
        }

        schedules[participantId] = schedule;
    }

    private int NextAttempts(int participantId)
        => schedules.TryGetValue(participantId, out var existing) ? existing.Attempts + 1 : 1;

    private DateTimeOffset ComputeRetry(DateTimeOffset now, int attempts, TimeSpan? retryAfter)
    {
        var seconds = Math.Min(
            settings.RetryMaxDelaySeconds,
            settings.RetryBaseDelaySeconds * Math.Pow(2, Math.Max(0, attempts - 1)));
        var delay = TimeSpan.FromSeconds(seconds);
        if (retryAfter is { } after && after > delay)
        {
            delay = after;
        }

        return now + delay;
    }

    private void SetError(int participantId, string message, DateTimeOffset? nextRetryAt)
        => runtimeState.Set(participantId, new AiTraderRuntimeSnapshot(
            AiTraderRuntimeStatus.Error, message, null, null, null, timeProvider.GetUtcNow().UtcDateTime,
            nextRetryAt?.UtcDateTime));

    private sealed class ParticipantSchedule
    {
        public int? LastCycleNumber { get; set; }

        public DateTimeOffset? NextRetryAt { get; set; }

        public int Attempts { get; set; }
    }
}
