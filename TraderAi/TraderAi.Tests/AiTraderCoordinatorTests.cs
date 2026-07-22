using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AiTraderCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string ValidDecision =
        "{\"summary\":\"Buy a strong company.\",\"cancelOrderIds\":[],\"bigInvestment\":null,\"orders\":[{\"side\":\"Buy\",\"companyId\":COMPANY,\"quantity\":2,\"limitPrice\":100,\"reason\":\"r\"}],\"predictions\":[]}";

    private readonly string databasePath;
    private readonly ServiceProvider provider;
    private readonly FakeProviderClient fakeClient = new();
    private readonly string tempDocsRoot;

    public AiTraderCoordinatorTests()
    {
        // A file-backed database, not a single shared in-memory connection, so each DI scope opens its own
        // connection and SQLite serialises the background task's writes against the scan's reads, matching how
        // the coordinator runs in production against a connection string.
        databasePath = Path.Combine(Path.GetTempPath(), "ai-coord-db-" + Guid.NewGuid().ToString("N") + ".db");
        tempDocsRoot = CreateTempDocs();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton(new FixedTimeProvider(Now));
        services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FixedTimeProvider>());
        services.AddSingleton(Random.Shared);
        services.AddSingleton<MarketCycleLock>();
        services.AddSingleton<AiTraderRuntimeState>();
        services.AddSingleton<AiProviderCatalog>();
        services.AddSingleton<AiPromptDocumentationProvider>();
        services.AddSingleton<IHostEnvironment>(new CoordinatorHostEnvironment());
        services.AddSingleton<IAiProviderClient>(fakeClient);
        services.AddScoped<IDecisionEngine, NoOpDecisionEngine>();
        services.AddScoped<MatchingEngine>();
        services.AddScoped<MarginService>();
        services.AddScoped<AutomatedBuyOrderPolicy>();
        services.AddScoped<TradingClockService>();
        services.AddScoped<MarketImpactService>();
        services.AddScoped<BigInvestmentService>();
        services.AddScoped<MarketService>();
        services.AddScoped<AiMarketSnapshotBuilder>();
        services.AddScoped<AiTradingPromptBuilder>();
        services.AddScoped<AiTraderCallService>();

        services.Configure<AiTradingOptions>(options =>
        {
            options.Enabled = true;
            options.DocumentationRoot = tempDocsRoot;
            options.MaxOrdersPerDecision = 10;
            options.HistoryCycles = 30;
            options.MaxConcurrentRequests = 4;
            options.RetryBaseDelaySeconds = 5;
            options.RetryMaxDelaySeconds = 300;
            options.AuthErrorRetrySeconds = 900;
            options.Providers["glm"] = new AiProviderOptions
            {
                DisplayName = "GLM",
                Endpoint = "https://glm.test/v1",
                ApiKey = "secret-key",
                Models = { "glm-4.6" },
            };
        });
        services.Configure<TradeFeeOptions>(_ => { });
        services.Configure<SettlementOptions>(_ => { });
        services.Configure<MarginOptions>(options => options.Enabled = false);
        services.Configure<AutomatedTradingOptions>(_ => { });
        services.Configure<VolatilityHaltOptions>(_ => { });
        services.Configure<RandomChanceRatesOptions>(_ => { });
        services.Configure<BigInvestmentOptions>(options => options.Enabled = true);
        services.Configure<TradingClockOptions>(options =>
        {
            options.TradingCyclesPerDay = 210;
            options.TradingCycleSeconds = 2;
            options.BreakDurationSeconds = 60;
        });

        provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        }
    }

    [Fact]
    public async Task DisabledCoordinatorStartsNoWork()
    {
        await SeedMarketAsync();
        Settings().Enabled = false;
        var coordinator = Coordinator();

        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(0, await Db().AiTraderCalls.CountAsync());
    }

    [Fact]
    public async Task StartupAbandonsOrphanedPendingCalls()
    {
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiTraderCalls.Add(new AiTraderCall
            {
                ParticipantId = 1,
                ParticipantName = "T",
                ProviderId = "glm",
                ProviderLabel = "GLM",
                Model = "glm-4.6",
                PromptHash = "h",
                RequestJson = "{}",
                Status = AiTraderCallStatus.Pending,
                RequestedAt = Now.UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        await Coordinator().AbandonStalePendingCallsAsync(CancellationToken.None);

        Assert.Equal(AiTraderCallStatus.Abandoned, (await Db().AiTraderCalls.SingleAsync()).Status);
    }

    [Fact]
    public async Task SuccessAppliesOrdersAndCompletes()
    {
        var seed = await SeedMarketAsync();
        fakeClient.OnSend = _ => Task.FromResult(Success(ValidDecision.Replace("COMPANY", seed.CompanyId.ToString())));

        var status = await Coordinator().ProcessParticipantAsync(seed.ParticipantId, seed.CycleNumber, CancellationToken.None);

        Assert.Equal(AiTraderCallStatus.Completed, status);
        var call = await Db().AiTraderCalls.SingleAsync();
        Assert.Equal(AiTraderCallStatus.Completed, call.Status);
        Assert.Equal(1, call.AppliedOrders);
        Assert.Equal(1, await Db().Orders.CountAsync());
        Assert.Equal(AiTraderRuntimeStatus.Waiting, Runtime().Get(seed.ParticipantId).Status);
    }

    [Fact]
    public async Task ProviderErrorSetsErrorStateAndCreatesNoOrder()
    {
        var seed = await SeedMarketAsync();
        fakeClient.OnSend = _ => Task.FromResult(HttpError(500));

        var status = await Coordinator().ProcessParticipantAsync(seed.ParticipantId, seed.CycleNumber, CancellationToken.None);

        Assert.Equal(AiTraderCallStatus.HttpError, status);
        Assert.Equal(0, await Db().Orders.CountAsync());
        Assert.Equal(AiTraderRuntimeStatus.Error, Runtime().Get(seed.ParticipantId).Status);
    }

    [Fact]
    public async Task AuthErrorUsesLongerRetryWindow()
    {
        var seed = await SeedMarketAsync();
        fakeClient.OnSend = _ => Task.FromResult(HttpError(401));

        await Coordinator().ProcessParticipantAsync(seed.ParticipantId, seed.CycleNumber, CancellationToken.None);

        var runtime = Runtime().Get(seed.ParticipantId);
        Assert.Equal(AiTraderRuntimeStatus.Error, runtime.Status);
        Assert.NotNull(runtime.NextRetryAt);
        Assert.Equal(Now.AddSeconds(900).UtcDateTime, runtime.NextRetryAt);
    }

    [Fact]
    public async Task InvalidJsonRetainsRawResponseAndSetsError()
    {
        var seed = await SeedMarketAsync();
        Settings().MaxInvalidJsonRetries = 0;
        const string malformed = "{\"summary\":\"x\",\"orders\":}";
        fakeClient.OnSend = _ => Task.FromResult(Success(malformed));

        var status = await Coordinator().ProcessParticipantAsync(seed.ParticipantId, seed.CycleNumber, CancellationToken.None);

        Assert.Equal(AiTraderCallStatus.InvalidJson, status);
        var call = await Db().AiTraderCalls.SingleAsync();
        Assert.Equal(malformed, call.ResponseBody);
        Assert.NotNull(call.Error);
        Assert.Equal(AiTraderRuntimeStatus.Error, Runtime().Get(seed.ParticipantId).Status);
    }

    [Fact]
    public async Task InvalidJsonIsRetriedWithinTheSameCycleThenCompletes()
    {
        var seed = await SeedMarketAsync();
        Settings().MaxInvalidJsonRetries = 1;
        var valid = ValidDecision.Replace("COMPANY", seed.CompanyId.ToString());
        var calls = 0;
        fakeClient.OnSend = _ =>
        {
            calls++;
            return Task.FromResult(Success(calls == 1 ? "{\"summary\":\"x\",\"orders\":}" : valid));
        };

        var status = await Coordinator().ProcessParticipantAsync(seed.ParticipantId, seed.CycleNumber, CancellationToken.None);

        Assert.Equal(AiTraderCallStatus.Completed, status);
        Assert.Equal(2, calls);
        Assert.Equal(2, await Db().AiTraderCalls.CountAsync());
        Assert.Equal(1, await Db().Orders.CountAsync());
        Assert.Equal(AiTraderRuntimeStatus.Waiting, Runtime().Get(seed.ParticipantId).Status);
    }

    [Fact]
    public async Task InvalidJsonRetriesAreBoundedThenSurfaceTheError()
    {
        var seed = await SeedMarketAsync();
        Settings().MaxInvalidJsonRetries = 2;
        var calls = 0;
        fakeClient.OnSend = _ =>
        {
            calls++;
            return Task.FromResult(Success("{\"summary\":\"x\",\"orders\":}"));
        };

        var status = await Coordinator().ProcessParticipantAsync(seed.ParticipantId, seed.CycleNumber, CancellationToken.None);

        Assert.Equal(AiTraderCallStatus.InvalidJson, status);
        Assert.Equal(3, calls);
        Assert.Equal(3, await Db().AiTraderCalls.CountAsync());
        Assert.Equal(0, await Db().Orders.CountAsync());
        Assert.Equal(AiTraderRuntimeStatus.Error, Runtime().Get(seed.ParticipantId).Status);
    }

    [Fact]
    public async Task PendingRowIsWrittenBeforeProviderIsEntered()
    {
        var seed = await SeedMarketAsync();
        var pendingSeen = false;
        fakeClient.OnSend = _ =>
        {
            using var scope = provider.CreateScope();
            pendingSeen = scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .AiTraderCalls.Any(call => call.Status == AiTraderCallStatus.Pending);
            return Task.FromResult(Success(ValidDecision.Replace("COMPANY", seed.CompanyId.ToString())));
        };

        await Coordinator().ProcessParticipantAsync(seed.ParticipantId, seed.CycleNumber, CancellationToken.None);

        Assert.True(pendingSeen);
    }

    [Fact]
    public async Task BlockedSendDoesNotHoldTheMarketLock()
    {
        var seed = await SeedMarketAsync();
        var gate = new TaskCompletionSource<AiProviderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeClient.OnSend = _ => gate.Task;

        var processing = Coordinator().ProcessParticipantAsync(seed.ParticipantId, seed.CycleNumber, CancellationToken.None);

        var lockField = provider.GetRequiredService<MarketCycleLock>();
        var acquired = await lockField.Semaphore.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(acquired);
        lockField.Semaphore.Release();

        gate.SetResult(Success(ValidDecision.Replace("COMPANY", seed.CompanyId.ToString())));
        await processing;
    }

    [Fact]
    public async Task GlobalConcurrencyIsCapped()
    {
        Settings().MaxConcurrentRequests = 1;
        var seed = await SeedMarketAsync(extraTraders: 2);
        var gate = new TaskCompletionSource<AiProviderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeClient.OnSend = _ => gate.Task;
        var coordinator = Coordinator();

        await coordinator.ScanAsync(CancellationToken.None);
        await Task.Delay(200);
        Assert.True(fakeClient.MaxConcurrent <= 1);

        var tasks = seed.AllParticipantIds
            .Select(coordinator.InFlightTaskFor)
            .Where(task => task is not null)
            .Select(task => task!)
            .ToList();
        gate.SetResult(Success(ValidDecision.Replace("COMPANY", seed.CompanyId.ToString())));
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ConcurrencyLimitChangesDoNotCreateAnOverlappingLimiter()
    {
        Settings().MaxConcurrentRequests = 1;
        await SeedMarketAsync();
        var gate = new TaskCompletionSource<AiProviderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeClient.OnSend = _ => gate.Task;
        var coordinator = Coordinator();

        await coordinator.ScanAsync(CancellationToken.None);
        await fakeClient.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, fakeClient.MaxConcurrent);

        await AddAiTradersAsync(2);
        Settings().MaxConcurrentRequests = 2;
        await coordinator.ScanAsync(CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(1, fakeClient.MaxConcurrent);
        Assert.Single(coordinator.InFlightParticipants);
        var drainingTasks = coordinator.InFlightParticipants
            .Select(coordinator.InFlightTaskFor)
            .Where(task => task is not null)
            .Select(task => task!)
            .ToList();
        gate.SetResult(HttpError(500));
        await Task.WhenAll(drainingTasks);
        Assert.Empty(coordinator.InFlightParticipants);

        var updatedGate = new TaskCompletionSource<AiProviderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeClient.OnSend = _ => updatedGate.Task;
        await coordinator.ScanAsync(CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(2, fakeClient.MaxConcurrent);
        var updatedTasks = coordinator.InFlightParticipants
            .Select(coordinator.InFlightTaskFor)
            .Where(task => task is not null)
            .Select(task => task!)
            .ToList();
        updatedGate.SetResult(HttpError(500));
        await Task.WhenAll(updatedTasks);
    }

    [Fact]
    public async Task ScanKeepsAtMostOneCallInFlightPerParticipant()
    {
        var seed = await SeedMarketAsync();
        var gate = new TaskCompletionSource<AiProviderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeClient.OnSend = _ => gate.Task;
        var coordinator = Coordinator();

        await coordinator.ScanAsync(CancellationToken.None);
        var task = coordinator.InFlightTaskFor(seed.ParticipantId);
        await coordinator.ScanAsync(CancellationToken.None);

        Assert.Single(coordinator.InFlightParticipants);

        gate.SetResult(Success(ValidDecision.Replace("COMPANY", seed.CompanyId.ToString())));
        if (task is not null)
        {
            await task;
        }
    }

    [Fact]
    public async Task ConfigurationEditedDuringCallIsNotApplied()
    {
        var seed = await SeedMarketAsync();
        fakeClient.OnSend = _ =>
        {
            // Simulate an operator editing the provider/model/key while the request is in flight: the revision
            // in the database advances past the one captured when the request was prepared.
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var configuration = db.AiTraderConfigurations.Single(entry => entry.ParticipantId == seed.ParticipantId);
            configuration.Revision += 1;
            db.SaveChanges();
            return Task.FromResult(Success(ValidDecision.Replace("COMPANY", seed.CompanyId.ToString())));
        };

        var status = await Coordinator().ProcessParticipantAsync(seed.ParticipantId, seed.CycleNumber, CancellationToken.None);

        Assert.Equal(AiTraderCallStatus.Completed, status);
        Assert.Equal(0, await Db().Orders.CountAsync());
    }

    [Fact]
    public async Task ScanStartsNoWorkOutsideScheduledDecisionCycles()
    {
        // Trading cycle 50 is not in the default cadence {2, 101, 200}.
        await SeedMarketAsync(tradingCycleNumber: 50);
        var gate = new TaskCompletionSource<AiProviderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeClient.OnSend = _ => gate.Task;
        var coordinator = Coordinator();

        await coordinator.ScanAsync(CancellationToken.None);

        Assert.Empty(coordinator.InFlightParticipants);
        Assert.Equal(0, await Db().AiTraderCalls.CountAsync());
    }

    [Fact]
    public async Task ScanUsesTheCurrentTradingDayLengthAfterCoordinatorCreation()
    {
        await SeedMarketAsync(tradingCycleNumber: 10);
        var gate = new TaskCompletionSource<AiProviderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeClient.OnSend = _ => gate.Task;
        var clockOptions = new SwappableOptions<TradingClockOptions>(new TradingClockOptions
        {
            TradingCyclesPerDay = 210,
            TradingCycleSeconds = 2,
            BreakDurationSeconds = 60,
        });
        var coordinator = Coordinator(clockOptions);
        clockOptions.Value = new TradingClockOptions
        {
            TradingCyclesPerDay = 20,
            TradingCycleSeconds = 2,
            BreakDurationSeconds = 60,
        };

        await coordinator.ScanAsync(CancellationToken.None);

        var inFlight = Assert.Single(coordinator.InFlightParticipants);
        gate.SetResult(HttpError(500));
        await coordinator.InFlightTaskFor(inFlight)!;
    }

    [Fact]
    public async Task FinalDecisionOfDayDefersOrdersToTheNextDay()
    {
        var seed = await SeedMarketAsync(tradingCycleNumber: 200);
        fakeClient.OnSend = _ => Task.FromResult(Success(ValidDecision.Replace("COMPANY", seed.CompanyId.ToString())));

        var status = await Coordinator().ProcessParticipantAsync(
            seed.ParticipantId, seed.CycleNumber, CancellationToken.None, isFinalDecisionOfDay: true);

        Assert.Equal(AiTraderCallStatus.Completed, status);
        var call = await Db().AiTraderCalls.SingleAsync();
        Assert.Equal(AiTraderCallStatus.PendingNextDay, call.Status);
        Assert.Equal(2, call.NextDayTargetDayNumber);
        Assert.Equal(0, await Db().Orders.CountAsync());
    }

    [Fact]
    public async Task FinalDecisionDefersBigInvestmentAndAppliesItAtTheNextOpen()
    {
        var seed = await SeedMarketAsync(tradingCycleNumber: 200);
        await using (var db = Db())
        {
            var participant = await db.Participants.SingleAsync(candidate => candidate.Id == seed.ParticipantId);
            participant.CurrentBalance = 100_000m;
            participant.SettledCashBalance = 100_000m;
            await db.SaveChangesAsync();
        }
        var decision =
            $"{{\"summary\":\"Fund Acme.\",\"cancelOrderIds\":[],\"bigInvestment\":{{\"companyId\":{seed.CompanyId},\"amount\":50000,\"reason\":\"growth\"}},\"orders\":[],\"predictions\":[]}}";
        fakeClient.OnSend = _ => Task.FromResult(Success(decision));
        var coordinator = Coordinator();

        var status = await coordinator.ProcessParticipantAsync(
            seed.ParticipantId, seed.CycleNumber, CancellationToken.None, isFinalDecisionOfDay: true);

        Assert.Equal(AiTraderCallStatus.Completed, status);
        Assert.Contains("big investment", Runtime().Get(seed.ParticipantId).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await Db().CompanyInvestments.CountAsync());

        await coordinator.ApplyPendingNextDayPlanAsync(
            seed.ParticipantId, seed.CycleNumber + 1, currentDayNumber: 2, CancellationToken.None);

        Assert.Equal(1, await Db().CompanyInvestments.CountAsync());
        Assert.Equal(AiTraderCallStatus.Completed, (await Db().AiTraderCalls.SingleAsync()).Status);
    }

    [Fact]
    public async Task DayOpenAppliesADuePendingPlan()
    {
        var seed = await SeedMarketAsync(tradingCycleNumber: 1);
        await SeedPendingPlanAsync(seed.ParticipantId, targetDayNumber: 1, seed.CompanyId);

        await Coordinator().ApplyPendingNextDayPlanAsync(
            seed.ParticipantId, seed.CycleNumber, currentDayNumber: 1, CancellationToken.None);

        var call = await Db().AiTraderCalls.SingleAsync();
        Assert.Equal(AiTraderCallStatus.Completed, call.Status);
        Assert.Equal(1, call.AppliedOrders);
        Assert.NotNull(call.AppliedAt);
        Assert.Equal(1, await Db().Orders.CountAsync());
    }

    [Fact]
    public async Task PendingPlanWhoseTargetDayPassedIsAbandonedWithoutOrders()
    {
        var seed = await SeedMarketAsync(tradingCycleNumber: 1);
        await SeedPendingPlanAsync(seed.ParticipantId, targetDayNumber: 1, seed.CompanyId);

        // The market has reached day 2's open; the plan targeted day 1, which never opened for it.
        await Coordinator().ApplyPendingNextDayPlanAsync(
            seed.ParticipantId, seed.CycleNumber, currentDayNumber: 2, CancellationToken.None);

        var call = await Db().AiTraderCalls.SingleAsync();
        Assert.Equal(AiTraderCallStatus.Abandoned, call.Status);
        Assert.NotNull(call.Error);
        Assert.Equal(0, await Db().Orders.CountAsync());
    }

    private async Task SeedPendingPlanAsync(int participantId, int targetDayNumber, int companyId)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AiTraderCalls.Add(new AiTraderCall
        {
            ParticipantId = participantId,
            ParticipantName = "AI 0",
            ProviderId = "glm",
            ProviderLabel = "GLM",
            Model = "glm-4.6",
            ConfigurationRevision = 1,
            PromptHash = "h",
            RequestJson = "{}",
            DecisionJson = ValidDecision.Replace("COMPANY", companyId.ToString()),
            Status = AiTraderCallStatus.PendingNextDay,
            NextDayTargetDayNumber = targetDayNumber,
            RequestedAt = Now.UtcDateTime,
            RespondedAt = Now.UtcDateTime,
        });
        await db.SaveChangesAsync();
    }

    private AiTraderCoordinator Coordinator(IOptions<TradingClockOptions>? clockOptions = null) => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        provider.GetRequiredService<AiProviderCatalog>(),
        provider.GetRequiredService<AiTraderRuntimeState>(),
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiTradingOptions>>(),
        clockOptions ?? provider.GetRequiredService<IOptions<TradingClockOptions>>(),
        provider.GetRequiredService<TimeProvider>(),
        provider.GetRequiredService<ILogger<AiTraderCoordinator>>());

    private AppDbContext Db() => provider.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    private AiTraderRuntimeState Runtime() => provider.GetRequiredService<AiTraderRuntimeState>();

    private AiTradingOptions Settings()
        => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiTradingOptions>>().Value;

    private sealed class SwappableOptions<T>(T value) : IOptions<T>
        where T : class
    {
        public T Value { get; set; } = value;
    }

    private async Task AddAiTradersAsync(int count)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var index = 0; index < count; index++)
        {
            var trader = new Participant
            {
                Name = $"Added AI {index}",
                Type = ParticipantType.AIAgent,
                IsActive = true,
                CurrentBalance = 10_000m,
                SettledCashBalance = 10_000m,
            };
            db.Participants.Add(trader);
            await db.SaveChangesAsync();
            db.AiTraderConfigurations.Add(new AiTraderConfiguration
            {
                ParticipantId = trader.Id,
                ProviderId = "glm",
                Model = "glm-4.6",
                Revision = 1,
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime,
            });
        }

        await db.SaveChangesAsync();
    }

    // The seeded cycle defaults to trading cycle 2, a scheduled decision cycle for the default cadence, so a scan
    // starts a fresh decision. Tests that exercise the day open or the end-of-day planning call pass 1 or 200.
    private async Task<Seed> SeedMarketAsync(int extraTraders = 0, int tradingCycleNumber = 2)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var day = new TradingDay { DayNumber = 1, State = TradingSessionState.Trading, OpenedInCycleId = 0 };
        db.TradingDays.Add(day);
        await db.SaveChangesAsync();
        var cycle = new MarketCycle { CycleNumber = tradingCycleNumber, TradingDayId = day.Id, TradingCycleNumber = tradingCycleNumber, Status = CycleStatus.Running };
        var market = new Market { Name = "Market", Status = MarketStatus.Running };
        var industry = new Industry { Name = "Tech" };
        db.AddRange(cycle, market, industry);
        await db.SaveChangesAsync();

        var company = new Company { Name = "Acme", IndustryId = industry.Id, IssuedSharesCount = 1_000 };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.PriceSnapshots.Add(new PriceSnapshot { CompanyId = company.Id, Price = 100m, Capitalization = 100_000m, CreatedInCycleId = cycle.Id });

        var ids = new List<int>();
        var primaryId = 0;
        for (var index = 0; index <= extraTraders; index++)
        {
            var trader = new Participant
            {
                Name = $"AI {index}",
                Type = ParticipantType.AIAgent,
                IsActive = true,
                CurrentBalance = 10_000m,
                SettledCashBalance = 10_000m,
            };
            db.Participants.Add(trader);
            await db.SaveChangesAsync();
            db.AiTraderConfigurations.Add(new AiTraderConfiguration
            {
                ParticipantId = trader.Id,
                ProviderId = "glm",
                Model = "glm-4.6",
                Revision = 1,
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime,
            });
            ids.Add(trader.Id);
            if (index == 0)
            {
                primaryId = trader.Id;
            }
        }

        day.OpenedInCycleId = cycle.Id;
        market.CurrentCycleId = cycle.Id;
        market.CurrentTradingDayId = day.Id;
        await db.SaveChangesAsync();

        return new Seed(primaryId, company.Id, cycle.CycleNumber, ids);
    }

    private static AiProviderResponse Success(string content)
        => new(AiProviderCallOutcome.Success, 200, content, content, 1, 1, 2, null, null);

    private static AiProviderResponse HttpError(int status)
        => new(AiProviderCallOutcome.HttpError, status, "error body", null, null, null, null, null, "http error");

    private static string CreateTempDocs()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-coord-" + Guid.NewGuid().ToString("N"));
        foreach (var document in new[]
        {
            "roles/ai-agent.md", "roles/individual.md", "rules/share-price-formation.md", "rules/trading-days.md",
            "rules/luld.md", "logic/settlement.md", "logic/margin.md", "logic/bank-loans.md", "logic/sector-sentiment.md",
        })
        {
            var full = Path.Combine(root, document);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "rule");
        }

        return root;
    }

    public void Dispose()
    {
        provider.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }.Where(File.Exists))
        {
            File.Delete(path);
        }

        if (Directory.Exists(tempDocsRoot))
        {
            Directory.Delete(tempDocsRoot, recursive: true);
        }
    }

    private sealed record Seed(int ParticipantId, int CompanyId, int CycleNumber, IReadOnlyList<int> AllParticipantIds);

    private sealed class NoOpDecisionEngine : IDecisionEngine
    {
        public IReadOnlyList<OrderIntent> Decide(DecisionContext context) => [];
    }

    private sealed class FakeProviderClient : IAiProviderClient
    {
        private int current;
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<CancellationToken, Task<AiProviderResponse>> OnSend { get; set; } =
            _ => Task.FromResult(new AiProviderResponse(AiProviderCallOutcome.Success, 200, "{}", "{}", 1, 1, 2, null, null));

        public int MaxConcurrent { get; private set; }

        public Task Entered => entered.Task;

        public PreparedAiProviderRequest Prepare(AiProviderDescriptor provider, string model, string systemMessage, string userMessage)
            => new(provider.Id, provider.Label, model, provider.Endpoint, "{\"prompt\":true}");

        public Task<AiProviderResponse> SendTestAsync(AiProviderDescriptor provider, string model, string apiKey, CancellationToken cancellationToken)
            => OnSend(cancellationToken);

        public async Task<AiProviderResponse> SendAsync(PreparedAiProviderRequest prepared, string apiKey, CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref current);
            MaxConcurrent = Math.Max(MaxConcurrent, concurrent);
            entered.TrySetResult();
            try
            {
                return await OnSend(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref current);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CoordinatorHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "TraderAi.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
