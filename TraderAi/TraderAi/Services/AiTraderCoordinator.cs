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
    IOptions<TradingClockOptions> clockOptions,
    TimeProvider timeProvider,
    ILogger<AiTraderCoordinator> logger) : BackgroundService
{
    private readonly object concurrencySync = new();
    private SemaphoreSlim concurrency = new(
        Math.Max(1, options.Value.MaxConcurrentRequests),
        Math.Max(1, options.Value.MaxConcurrentRequests));
    private int concurrencyLimit = Math.Max(1, options.Value.MaxConcurrentRequests);
    private readonly ConcurrentDictionary<int, Task> inFlight = new();
    private readonly ConcurrentDictionary<int, ParticipantSchedule> schedules = new();

    public IReadOnlyCollection<int> InFlightParticipants => inFlight.Keys.ToArray();

    public Task? InFlightTaskFor(int participantId) => inFlight.GetValueOrDefault(participantId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recoveredPendingCalls = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = options.Value;
                if (settings.Enabled && !recoveredPendingCalls)
                {
                    await AbandonStalePendingCallsAsync(stoppingToken);
                    recoveredPendingCalls = true;
                }

                var delay = settings.Enabled
                    ? TimeSpan.FromMilliseconds(Math.Max(1, settings.ScanIntervalMilliseconds))
                    : TimeSpan.FromMilliseconds(250);
                await Task.Delay(delay, stoppingToken);
                if (!options.Value.Enabled)
                {
                    continue;
                }

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

    // One scan pass. A trader with a call in flight or an unelapsed retry window is skipped, and each cycle acts at
    // most once. At the day's opening cycle a due deferred plan is applied; at a scheduled decision cycle a fresh
    // decision starts, flagged as the day's final (planning) call when it is the last scheduled cycle.
    public async Task ScanAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        if (!TryRefreshConcurrencyLimit(settings.MaxConcurrentRequests))
        {
            return;
        }

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

        var currentDayNumber = await dbContext.TradingDays
            .Where(day => day.Id == currentCycle.TradingDayId)
            .Select(day => day.DayNumber)
            .FirstOrDefaultAsync(stoppingToken);

        var eligible = await (
            from configuration in dbContext.AiTraderConfigurations
            join participant in dbContext.Participants on configuration.ParticipantId equals participant.Id
            where participant.IsActive && !participant.IsBankrupt && participant.Type == ParticipantType.AIAgent
            select new { configuration.ParticipantId, configuration.MaxDecisionsPerDay }).ToListAsync(stoppingToken);

        var now = timeProvider.GetUtcNow();
        foreach (var agent in eligible)
        {
            var participantId = agent.ParticipantId;
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

            // Cycle 1 is reserved for applying the prior day's deferred plan; it is never a fresh-decision cycle.
            if (currentCycle.TradingCycleNumber == 1)
            {
                var hasDuePlan = await dbContext.AiTraderCalls.AnyAsync(
                    call => call.ParticipantId == participantId
                        && (call.MarketRunId == market.CurrentRunId || call.MarketRunId == null)
                        && call.Status == AiTraderCallStatus.PendingNextDay
                        && call.NextDayTargetDayNumber != null
                        && call.NextDayTargetDayNumber <= currentDayNumber,
                    stoppingToken);
                if (hasDuePlan)
                {
                    StartApplyingPendingPlan(participantId, currentCycle.CycleNumber, currentDayNumber, stoppingToken);
                }

                continue;
            }

            var tradingCyclesPerDay = Math.Max(1, clockOptions.Value.TradingCyclesPerDay);
            var decisionCycles = AiDecisionCadence.DecisionCycles(agent.MaxDecisionsPerDay, tradingCyclesPerDay);
            if (decisionCycles.Count == 0 || !decisionCycles.Contains(currentCycle.TradingCycleNumber))
            {
                continue;
            }

            var isFinalDecisionOfDay = currentCycle.TradingCycleNumber == decisionCycles[^1];
            StartProcessing(participantId, currentCycle.CycleNumber, isFinalDecisionOfDay, stoppingToken);
        }
    }

    private void StartProcessing(int participantId, int cycleNumber, bool isFinalDecisionOfDay, CancellationToken stoppingToken)
        => RunExclusive(
            participantId,
            token => ProcessParticipantAsync(participantId, cycleNumber, token, isFinalDecisionOfDay),
            stoppingToken);

    private void StartApplyingPendingPlan(int participantId, int cycleNumber, int currentDayNumber, CancellationToken stoppingToken)
        => RunExclusive(
            participantId,
            token => ApplyPendingNextDayPlanAsync(participantId, cycleNumber, currentDayNumber, token),
            stoppingToken);

    private void RunExclusive(int participantId, Func<CancellationToken, Task> action, CancellationToken stoppingToken)
    {
        var task = Task.Run(async () =>
        {
            var concurrencyGate = CurrentConcurrencyGate();
            await concurrencyGate.WaitAsync(stoppingToken);
            try
            {
                await action(stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AI trader processing failed for participant {ParticipantId}.", participantId);
            }
            finally
            {
                concurrencyGate.Release();
                inFlight.TryRemove(participantId, out _);
            }
        }, stoppingToken);

        inFlight[participantId] = task;
    }

    private SemaphoreSlim CurrentConcurrencyGate()
    {
        lock (concurrencySync)
        {
            return concurrency;
        }
    }

    private bool TryRefreshConcurrencyLimit(int requestedLimit)
    {
        var limit = Math.Max(1, requestedLimit);
        lock (concurrencySync)
        {
            if (limit == concurrencyLimit)
            {
                return true;
            }

            if (!inFlight.IsEmpty)
            {
                return false;
            }

            concurrency.Dispose();
            concurrency = new SemaphoreSlim(limit, limit);
            concurrencyLimit = limit;
            return true;
        }
    }

    public async Task<AiTraderCallStatus> ProcessParticipantAsync(
        int participantId,
        int cycleNumber,
        CancellationToken stoppingToken,
        bool isFinalDecisionOfDay = false)
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

        var apiKey = catalog.FindApiKey(configuration.ProviderId);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SetError(participantId, "No API key is configured for this provider. Add it in Settings.", null);
            return AiTraderCallStatus.Abandoned;
        }

        var snapshot = await services.GetRequiredService<AiMarketSnapshotBuilder>()
            .BuildAsync(participantId, isFinalDecisionOfDay);
        if (snapshot is null)
        {
            return AiTraderCallStatus.Abandoned;
        }

        var market = await dbContext.Markets.FirstOrDefaultAsync(stoppingToken);
        var prompt = services.GetRequiredService<AiTradingPromptBuilder>().Build(snapshot);
        var client = services.GetRequiredService<IAiProviderClient>();
        var prepared = client.Prepare(provider, configuration.Model, prompt.SystemMessage, prompt.UserMessage);

        if (options.Value.LogSnapshotSizeBreakdown)
        {
            logger.LogInformation(
                "{SnapshotSizeReport}", AiSnapshotSizeReport.Build(snapshot, prompt.SystemMessage, prompt.UserMessage));
        }

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
            prepared.RequestJson,
            market?.CurrentRunId,
            snapshot.Market.TradingDayNumber,
            snapshot.Companies.ToDictionary(company => company.CompanyId, company => company.CurrentPrice));

        var callService = services.GetRequiredService<AiTraderCallService>();

        runtimeState.Set(participantId, new AiTraderRuntimeSnapshot(
            AiTraderRuntimeStatus.Thinking, "Thinking", null, snapshot.Market.CycleNumber,
            timeProvider.GetUtcNow().UtcDateTime, null, null));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken, runtimeState.BeginCall(participantId));

        AiProviderResponse? response = null;
        AiTraderCallExecution execution;
        var invalidJsonRetriesRemaining = Math.Max(0, options.Value.MaxInvalidJsonRetries);
        while (true)
        {
            execution = await callService.ExecuteAsync(
                descriptor,
                options.Value.MaxOrdersPerDecision,
                options.Value.MaxPredictionsPerDecision,
                options.Value.PredictionHorizonCycles,
                async token =>
                {
                    response = await client.SendAsync(prepared, apiKey, token);
                    return response;
                },
                linked.Token);

            // A malformed reply wastes the whole scheduled decision; retry the same request a bounded number of times
            // before surfacing the error, and stop early if the call was cancelled by a mid-flight configuration edit.
            if (execution.Status != AiTraderCallStatus.InvalidJson
                || invalidJsonRetriesRemaining <= 0
                || linked.Token.IsCancellationRequested)
            {
                break;
            }

            invalidJsonRetriesRemaining--;
        }

        if (options.Value.LogSnapshotSizeBreakdown && response?.PromptTokens is { } measuredPromptTokens)
        {
            logger.LogInformation(
                "AI provider {ProviderId}/{Model} measured prompt_tokens={PromptTokens} for participant {ParticipantId} (cycle {CycleNumber}).",
                configuration.ProviderId, configuration.Model, measuredPromptTokens, participantId, snapshot.Market.CycleNumber);
        }

        if (execution.Status == AiTraderCallStatus.Completed && execution.Decision is not null)
        {
            // The final call of the day is a planning call: its orders are stored and applied at the next day's
            // opening cycle rather than placed now.
            if (isFinalDecisionOfDay)
            {
                var targetDayNumber = snapshot.Market.TradingDayNumber + 1;
                await callService.MarkPendingNextDayAsync(execution.CallId, targetDayNumber);
                schedules[participantId] = new ParticipantSchedule { LastCycleNumber = cycleNumber };
                var investmentPlan = execution.Decision.BigInvestment is null
                    ? "no big investment"
                    : "a big investment";
                runtimeState.Set(participantId, new AiTraderRuntimeSnapshot(
                    AiTraderRuntimeStatus.Waiting,
                    $"Planned {investmentPlan} and {execution.Decision.Orders.Length} order(s) for the next trading day open.",
                    execution.CallId, snapshot.Market.CycleNumber, null, timeProvider.GetUtcNow().UtcDateTime, null));
                return execution.Status;
            }

            runtimeState.Set(participantId, new AiTraderRuntimeSnapshot(
                AiTraderRuntimeStatus.Applying, "Applying decision", execution.CallId, snapshot.Market.CycleNumber,
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
            var investmentOutcome = InvestmentOutcome(application.BigInvestment);
            runtimeState.Set(participantId, new AiTraderRuntimeSnapshot(
                AiTraderRuntimeStatus.Waiting, $"{investmentOutcome} Applied {applied} order(s), rejected {rejected}.",
                execution.CallId, snapshot.Market.CycleNumber, null, timeProvider.GetUtcNow().UtcDateTime, null));
            return execution.Status;
        }

        HandleFailure(participantId, cycleNumber, execution, response);
        return execution.Status;
    }

    // Applies (or clears) an agent's deferred plans at the day's opening cycle. A plan whose target day has arrived
    // is reapplied through the ordinary order path so it is revalidated against fresh state; a plan whose target day
    // already passed is abandoned so it never applies late.
    public async Task ApplyPendingNextDayPlanAsync(
        int participantId,
        int cycleNumber,
        int currentDayNumber,
        CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var callService = services.GetRequiredService<AiTraderCallService>();

        var configuration = await services.GetRequiredService<AppDbContext>().AiTraderConfigurations
            .FirstOrDefaultAsync(candidate => candidate.ParticipantId == participantId, stoppingToken);

        var duePlans = await callService.GetDuePendingNextDayCallsAsync(participantId, currentDayNumber);
        foreach (var plan in duePlans)
        {
            if (plan.NextDayTargetDayNumber < currentDayNumber)
            {
                await callService.AbandonPendingNextDayAsync(
                    plan.Id, "The target trading day passed before the market reopened.");
                continue;
            }

            var decision = callService.DeserializeDecision(plan.DecisionJson);
            if (configuration is null || decision is null)
            {
                await callService.AbandonPendingNextDayAsync(plan.Id, "The deferred plan could not be applied.");
                continue;
            }

            runtimeState.Set(participantId, new AiTraderRuntimeSnapshot(
                AiTraderRuntimeStatus.Applying, "Applying next-day plan", plan.Id, cycleNumber,
                timeProvider.GetUtcNow().UtcDateTime, null, null));

            AiDecisionApplicationResult application;
            using (var applyScope = scopeFactory.CreateScope())
            {
                application = await applyScope.ServiceProvider.GetRequiredService<MarketService>()
                    .ApplyAiDecisionAsync(participantId, plan.ConfigurationRevision, decision);
            }

            var applied = application.Orders.Count(order => order.Applied);
            var rejected = application.Orders.Length - applied;
            await callService.RecordApplicationAsync(
                plan.Id, callService.SerializeApplicationResult(application), applied, rejected);
            var investmentOutcome = InvestmentOutcome(application.BigInvestment);
            runtimeState.Set(participantId, new AiTraderRuntimeSnapshot(
                AiTraderRuntimeStatus.Waiting,
                $"{investmentOutcome} Applied {applied} next-day order(s), rejected {rejected}.",
                plan.Id, cycleNumber, null, timeProvider.GetUtcNow().UtcDateTime, null));
        }

        schedules[participantId] = new ParticipantSchedule { LastCycleNumber = cycleNumber };
    }

    private static string InvestmentOutcome(AiBigInvestmentApplicationResult? investment) => investment switch
    {
        null => "No big investment requested.",
        { Applied: true } => "Big investment applied.",
        _ => "Big investment rejected.",
    };

    private void HandleFailure(int participantId, int cycleNumber, AiTraderCallExecution execution, AiProviderResponse? response)
    {
        var now = timeProvider.GetUtcNow();
        var schedule = new ParticipantSchedule();

        switch (execution.Status)
        {
            case AiTraderCallStatus.HttpError when response?.HttpStatusCode is 401 or 403:
                schedule.NextRetryAt = now.AddSeconds(options.Value.AuthErrorRetrySeconds);
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
            options.Value.RetryMaxDelaySeconds,
            options.Value.RetryBaseDelaySeconds * Math.Pow(2, Math.Max(0, attempts - 1)));
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
