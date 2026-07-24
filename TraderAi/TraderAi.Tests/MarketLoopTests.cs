using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class MarketLoopTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly MarketService marketService;

    public MarketLoopTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
        marketService = new MarketService(context, new MatchingEngine(context), new DeterministicDecisionEngine(), new MarketCycleLock(), new Random(1));
    }

    [Fact]
    public async Task CycleTickDecidesAndAdvancesWhenMarketIsRunning()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        // The decision pass lists a buy and a sell in the first cycle; both rest one cycle before they can
        // cross, so the first tick only places them and the second tick matches them.
        var placingTick = await marketService.RunCycleTickAsync();

        Assert.True(placingTick.Ran);
        Assert.Equal(2, placingTick.OrdersPlaced);
        Assert.Equal(0, placingTick.FillCount);
        Assert.Equal(1, placingTick.CompletedCycleNumber);
        Assert.Equal(0, await context.ShareTransactions.CountAsync());

        var matchingTick = await marketService.RunCycleTickAsync();

        Assert.True(matchingTick.Ran);
        Assert.Equal(1, matchingTick.FillCount);
        Assert.Equal(2, matchingTick.CompletedCycleNumber);
        Assert.Equal(1, await context.ShareTransactions.CountAsync());
    }

    [Fact]
    public async Task CycleTickIsSkippedWhenMarketIsPaused()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        await marketService.SetStatusAsync(MarketStatus.Paused);

        var tick = await marketService.RunCycleTickAsync();

        Assert.False(tick.Ran);
        Assert.Equal(0, await context.Orders.CountAsync());
        Assert.Equal(0, await context.ShareTransactions.CountAsync());
    }

    [Fact]
    public async Task ResumingAfterPauseLetsCycleTicksRunAgain()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        await marketService.SetStatusAsync(MarketStatus.Paused);
        Assert.False((await marketService.RunCycleTickAsync()).Ran);

        await marketService.SetStatusAsync(MarketStatus.Running);
        var tick = await marketService.RunCycleTickAsync();

        Assert.True(tick.Ran);
    }

    [Fact]
    public async Task HostedLoopObservesEnabledSettingChangesWithoutRestarting()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var services = new ServiceCollection();
        services.AddSingleton(marketService);
        await using var provider = services.BuildServiceProvider();
        var options = new MutableOptions<MarketLoopOptions>(new MarketLoopOptions
        {
            Enabled = false,
            IntervalSeconds = 1,
        });
        var hostedLoop = new MarketLoopService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<MarketLoopService>.Instance);

        await hostedLoop.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        options.Value.Enabled = true;
        await Task.Delay(1_200);
        await hostedLoop.StopAsync(CancellationToken.None);

        Assert.True((await context.MarketCycles.OrderByDescending(cycle => cycle.CycleNumber).FirstAsync()).CycleNumber > 1);
    }

    [Fact]
    public async Task SetStatusReturnsNullWhenNoMarketExists()
    {
        Assert.Null(await marketService.SetStatusAsync(MarketStatus.Paused));
    }

    [Fact]
    public async Task DemoResetReplacesFinancialHistoryWithOneBaselinePerCompany()
    {
        var financialService = FinancialService(new Random(20260724));
        var clock = new TradingClockService(context, Options.Create(new TradingClockOptions()));
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            tradingClockService: clock,
            companyFinancialService: financialService);
        await service.SeedDemoMarketAsync();

        var oldCompany = await context.Companies.OrderBy(company => company.Id).FirstAsync();
        var oldCycle = await context.MarketCycles.SingleAsync();
        var dividend = new CompanyDividendEvent
        {
            CompanyId = oldCompany.Id,
            DeclaredAmount = 100m,
            FundedAmount = 100m,
            FundingOutcome = DividendFundingOutcome.Paid,
            IssuerCashBeforeFunding = 100m,
            CreatedInCycleId = oldCycle.Id,
            TradingDayNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        context.CompanyDividendEvents.Add(dividend);
        await context.SaveChangesAsync();
        var oldSnapshot = await context.CompanyFinancialSnapshots
            .SingleAsync(snapshot => snapshot.CompanyId == oldCompany.Id);
        oldSnapshot.LatestDividendEventId = dividend.Id;
        await context.SaveChangesAsync();
        var auditor = new Auditor
        {
            Name = "Reset chain auditor",
            Description = "Protects the complete audit evidence deletion order.",
            CreatedAt = DateTime.UtcNow,
        };
        context.Auditors.Add(auditor);
        await context.SaveChangesAsync();
        var rating = new CompanyRating
        {
            CompanyId = oldCompany.Id,
            AuditorId = auditor.Id,
            Rating = CompanyRiskRating.Stable,
            CreatedInCycleId = oldCycle.Id,
            CreatedAt = DateTime.UtcNow,
        };
        context.CompanyRatings.Add(rating);
        await context.SaveChangesAsync();
        context.CompanyAuditEvidence.Add(new CompanyAuditEvidence
        {
            CompanyRatingId = rating.Id,
            CompanyId = oldCompany.Id,
            CompanyFinancialSnapshotId = oldSnapshot.Id,
            LatestDividendEventId = dividend.Id,
            EvaluationStartTradingDayNumber = 1,
            EvaluationEndTradingDayNumber = 1,
            EffectiveTradingDayNumber = 2,
            IndustryTrend = IndustryTrend.Plateau,
        });
        await context.SaveChangesAsync();

        await service.ResetDemoMarketAsync();

        var financialCounts = await context.CompanyFinancialSnapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .Select(group => group.Count())
            .ToListAsync();
        Assert.Equal(await context.Companies.CountAsync(), financialCounts.Count);
        Assert.All(financialCounts, count => Assert.Equal(1, count));
        Assert.Empty(await context.CompanyAuditEvidence.ToListAsync());
        Assert.Empty(await context.CompanyRatings.ToListAsync());
        Assert.Empty(await context.CompanyDividendEvents.ToListAsync());
        Assert.DoesNotContain(
            await context.CompanyFinancialSnapshots.ToListAsync(),
            snapshot => snapshot.CompanyId == oldCompany.Id);
    }

    [Fact]
    public async Task DemoFinancialBaselineUsesTheSeededIndustrySentiment()
    {
        var financialService = FinancialService(new ConstantRandom(0.5d));
        var clock = new TradingClockService(context, Options.Create(new TradingClockOptions()));
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            industrySentimentOptions: Options.Create(new IndustrySentimentOptions { Enabled = true }),
            tradingClockService: clock,
            companyFinancialService: financialService);

        await service.SeedDemoMarketAsync();

        var rows = await (
                from snapshot in context.CompanyFinancialSnapshots
                join company in context.Companies on snapshot.CompanyId equals company.Id
                join industry in context.Industries on company.IndustryId equals industry.Id
                select new { industry.SentimentValue, snapshot.BusinessRiskScore })
            .ToListAsync();
        var rising = rows.Where(row => row.SentimentValue > 0).ToList();
        var falling = rows.Where(row => row.SentimentValue < 0).ToList();
        Assert.NotEmpty(rising);
        Assert.NotEmpty(falling);
        Assert.All(rising, row => Assert.True(row.BusinessRiskScore < 50m));
        Assert.All(falling, row => Assert.True(row.BusinessRiskScore > 50m));
    }

    [Fact]
    public async Task InitialSeedRollsBackLateFinancialFailureAndRetriesCleanly()
    {
        var failure = new FailOnceFinancialSnapshotInterceptor();
        using var seedConnection = new SqliteConnection("DataSource=:memory:");
        seedConnection.Open();
        var seedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(seedConnection)
            .AddInterceptors(failure)
            .Options;
        await using var seedContext = new AppDbContext(seedOptions);
        seedContext.Database.EnsureCreated();
        var financialOptions = new CompanyFinancialOptions();
        var financialService = new CompanyFinancialService(
            seedContext,
            Options.Create(financialOptions),
            Options.Create(new RandomChanceRatesOptions()),
            Options.Create(new TradingClockOptions()),
            new Random(20260724),
            new CompanyFinancialScorer(Options.Create(financialOptions)));
        var clock = new TradingClockService(
            seedContext,
            Options.Create(new TradingClockOptions()));
        var service = new MarketService(
            seedContext,
            new MatchingEngine(seedContext),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            tradingClockService: clock,
            companyFinancialService: financialService);

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => service.SeedDemoMarketAsync());
        Assert.Contains("Injected late financial seed failure.", error.ToString());
        seedContext.ChangeTracker.Clear();

        Assert.Empty(await seedContext.Markets.ToListAsync());
        Assert.Empty(await seedContext.MarketRuns.ToListAsync());
        Assert.Empty(await seedContext.MarketCycles.ToListAsync());
        Assert.Empty(await seedContext.TradingDays.ToListAsync());
        Assert.Empty(await seedContext.Companies.ToListAsync());
        Assert.Empty(await seedContext.Participants.ToListAsync());
        Assert.Empty(await seedContext.PriceSnapshots.ToListAsync());
        Assert.Empty(await seedContext.CompanyFinancialSnapshots.ToListAsync());

        await service.SeedDemoMarketAsync();

        Assert.Single(await seedContext.Markets.ToListAsync());
        Assert.Single(await seedContext.MarketRuns.ToListAsync());
        Assert.Single(await seedContext.MarketCycles.ToListAsync());
        Assert.Single(await seedContext.TradingDays.ToListAsync());
        Assert.Equal(100, await seedContext.Companies.CountAsync());
        Assert.Equal(600, await seedContext.Participants.CountAsync());
        Assert.Equal(100, await seedContext.PriceSnapshots.CountAsync());
        Assert.Equal(100, await seedContext.CompanyFinancialSnapshots.CountAsync());
    }

    [Fact]
    public async Task FirstRunningCheckpointRecoversDemoRosterSeededWhileFinancialsWereDisabled()
    {
        var financialOptions = new CompanyFinancialOptions { Enabled = false };
        var financialService = new CompanyFinancialService(
            context,
            Options.Create(financialOptions),
            Options.Create(new RandomChanceRatesOptions()),
            Options.Create(new TradingClockOptions()),
            new Random(20260724),
            new CompanyFinancialScorer(Options.Create(financialOptions)));
        var clock = new TradingClockService(
            context,
            Options.Create(new TradingClockOptions()));
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            tradingClockService: clock,
            companyFinancialService: financialService);
        await service.SeedDemoMarketAsync();
        var firstCycleId = await context.Markets.Select(market => market.CurrentCycleId).SingleAsync();
        Assert.Empty(await context.CompanyFinancialSnapshots.ToListAsync());

        financialOptions.Enabled = true;
        await service.SetStatusAsync(MarketStatus.Running);
        await service.RunCycleTickAsync();

        var rows = await context.CompanyFinancialSnapshots
            .AsNoTracking()
            .ToListAsync();
        Assert.Equal(200, rows.Count);
        Assert.Equal(100, rows.Count(snapshot =>
            snapshot.Moment == CompanyFinancialSnapshotMoment.Seed
            && snapshot.CreatedInCycleId == firstCycleId
            && snapshot.TradingDayNumber == 1));
        Assert.Equal(100, rows.Count(snapshot =>
            snapshot.Moment == CompanyFinancialSnapshotMoment.DayOpening
            && snapshot.CreatedInCycleId == firstCycleId
            && snapshot.TradingDayNumber == 1));
    }

    [Theory]
    [InlineData(1, CompanyFinancialSnapshotMoment.DayOpening, true)]
    [InlineData(105, CompanyFinancialSnapshotMoment.DayOpening, false)]
    [InlineData(106, CompanyFinancialSnapshotMoment.Midday, true)]
    [InlineData(107, CompanyFinancialSnapshotMoment.Midday, false)]
    public async Task FinancialReportingRunsOnlyAtDailyCheckpoints(
        int tradingCycleNumber,
        CompanyFinancialSnapshotMoment expectedMoment,
        bool expected)
    {
        var seed = await TestMarketSeed.SeedAccountingScenarioAsync(context);
        seed.Cycle.TradingCycleNumber = tradingCycleNumber;
        await context.SaveChangesAsync();
        var financialService = FinancialService(new Random(20260724));
        await financialService.StageSeedSnapshotAsync(
            seed.Company,
            100m,
            seed.Cycle.Id,
            seed.Day.DayNumber,
            DateTime.UtcNow);
        await context.SaveChangesAsync();
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            companyFinancialService: financialService);

        await service.RunCycleTickAsync();

        Assert.Equal(
            expected,
            await context.CompanyFinancialSnapshots
                .AnyAsync(snapshot => snapshot.Moment == expectedMoment));
        Assert.Equal(expected ? 2 : 1, await context.CompanyFinancialSnapshots.CountAsync());
    }

    [Fact]
    public async Task ScheduledFinancialSnapshotIsPersistedBeforeSameCycleLifecycleAndAuditWork()
    {
        var observer = new RecordingCommandInterceptor();
        using var observedConnection = new SqliteConnection("DataSource=:memory:");
        observedConnection.Open();
        var observedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(observedConnection)
            .AddInterceptors(observer)
            .Options;
        await using var observedContext = new AppDbContext(observedOptions);
        observedContext.Database.EnsureCreated();
        var seed = await TestMarketSeed.SeedAccountingScenarioAsync(observedContext);
        var financialOptions = new CompanyFinancialOptions();
        var financialService = new CompanyFinancialService(
            observedContext,
            Options.Create(financialOptions),
            Options.Create(new RandomChanceRatesOptions()),
            Options.Create(new TradingClockOptions()),
            new Random(20260724),
            new CompanyFinancialScorer(Options.Create(financialOptions)));
        await financialService.StageSeedSnapshotAsync(
            seed.Company,
            100m,
            seed.Cycle.Id,
            seed.Day.DayNumber,
            DateTime.UtcNow);
        await observedContext.SaveChangesAsync();
        var auditDay = new TradingDay
        {
            DayNumber = 3,
            State = TradingSessionState.Trading,
            OpenedInCycleId = 0,
        };
        observedContext.TradingDays.Add(auditDay);
        await observedContext.SaveChangesAsync();
        var auditCycle = new MarketCycle
        {
            CycleNumber = 3,
            TradingDayId = auditDay.Id,
            TradingCycleNumber = 1,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        observedContext.MarketCycles.Add(auditCycle);
        await observedContext.SaveChangesAsync();
        auditDay.OpenedInCycleId = auditCycle.Id;
        seed.Market.CurrentCycleId = auditCycle.Id;
        seed.Market.CurrentTradingDayId = auditDay.Id;
        await observedContext.SaveChangesAsync();
        observer.Clear();
        var lifecycle = new CompanyLifecycleService(
            observedContext,
            Options.Create(new CompanyLifecycleOptions { Enabled = true }),
            Options.Create(new RandomChanceRatesOptions()),
            new ConstantRandom(0d),
            new MarketImpactService(observedContext),
            financialService);
        var auditor = new AuditorService(
            observedContext,
            Options.Create(new AuditorOptions { Enabled = true }),
            Options.Create(new CompanyFinancialOptions()));
        var service = new MarketService(
            observedContext,
            new MatchingEngine(observedContext),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            companyLifecycleService: lifecycle,
            auditorService: auditor,
            companyFinancialService: financialService);

        await service.RunCycleTickAsync();

        var commands = observer.Commands.ToArray();
        var reportInsert = Array.FindIndex(
            commands,
            command => command.Contains("INSERT INTO \"CompanyFinancialSnapshots\"", StringComparison.Ordinal));
        var lifecycleRead = Array.FindIndex(
            commands,
            command => command.Contains("FROM \"Companies\" AS \"c\"", StringComparison.Ordinal)
                && command.Contains("\"c\".\"ClosedInCycleId\" IS NULL", StringComparison.Ordinal)
                && !command.Contains("JOIN", StringComparison.Ordinal));
        var auditRead = Array.FindIndex(
            commands,
            command => command.Contains("FROM \"Auditors\" AS \"a\"", StringComparison.Ordinal)
                && command.Contains("ORDER BY \"a\".\"Id\"", StringComparison.Ordinal));
        Assert.True(reportInsert >= 0);
        Assert.True(lifecycleRead >= 0);
        Assert.True(auditRead >= 0);
        Assert.True(reportInsert < lifecycleRead);
        Assert.True(reportInsert < auditRead);

        var companies = await observedContext.Companies.OrderBy(company => company.Id).ToListAsync();
        Assert.Equal(2, companies.Count);
        Assert.Contains(
            await observedContext.CompanyFinancialSnapshots.ToListAsync(),
            snapshot => snapshot.CompanyId == companies[0].Id
                && snapshot.Moment == CompanyFinancialSnapshotMoment.DayOpening);
        Assert.Contains(
            await observedContext.CompanyFinancialSnapshots.ToListAsync(),
            snapshot => snapshot.CompanyId == companies[1].Id
                && snapshot.Moment == CompanyFinancialSnapshotMoment.Seed);
        Assert.NotEmpty(await observedContext.CompanyRatings.ToListAsync());
    }

    [Fact]
    public async Task LateCycleFailureRollsBackMaintenanceAndAdvanceMutations()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        var firstCycle = await context.MarketCycles.SingleAsync();
        var market = await context.Markets.SingleAsync();
        var buyer = await context.Participants.SingleAsync(participant => participant.Name == "Bob");
        var company = await context.Companies.SingleAsync();

        for (var cycleNumber = 2; cycleNumber <= 17; cycleNumber++)
        {
            context.MarketCycles.Add(new MarketCycle
            {
                CycleNumber = cycleNumber,
                Status = CycleStatus.Running,
                StartedAt = DateTime.UtcNow,
            });
        }

        await context.SaveChangesAsync();
        var currentCycle = await context.MarketCycles.SingleAsync(cycle => cycle.CycleNumber == 16);
        var day = new TradingDay
        {
            DayNumber = 1,
            State = TradingSessionState.Trading,
            OpenedInCycleId = currentCycle.Id,
        };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();
        currentCycle.TradingDayId = day.Id;
        currentCycle.TradingCycleNumber = 1;
        market.CurrentCycleId = currentCycle.Id;
        market.NextDividendCycleNumber = currentCycle.CycleNumber;
        buyer.ReservedBalance = 100m;
        var staleOrder = new Order
        {
            ParticipantId = buyer.Id,
            CompanyId = company.Id,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 1,
            LimitPrice = 100m,
            ReservedCashAmount = 100m,
            CreatedInCycleId = firstCycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Orders.Add(staleOrder);
        await context.SaveChangesAsync();
        var financialService = FinancialService(new Random(20260724));
        await financialService.StageSeedSnapshotAsync(
            company,
            100m,
            currentCycle.Id,
            day.DayNumber,
            DateTime.UtcNow);
        await context.SaveChangesAsync();
        var companyCashBefore = company.CashBalance;
        var random = new CountingCorporateCashRoll();

        var service = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            random,
            companyFinancialService: financialService);

        await Assert.ThrowsAsync<DbUpdateException>(() => service.RunCycleTickAsync());

        context.ChangeTracker.Clear();
        var persistedOrder = await context.Orders.SingleAsync(order => order.Id == staleOrder.Id);
        var persistedBuyer = await context.Participants.SingleAsync(participant => participant.Id == buyer.Id);
        var persistedMarket = await context.Markets.SingleAsync();
        var persistedCurrentCycle = await context.MarketCycles.SingleAsync(cycle => cycle.Id == currentCycle.Id);

        Assert.Equal(OrderStatus.Open, persistedOrder.Status);
        Assert.Equal(100m, persistedOrder.ReservedCashAmount);
        Assert.Equal(100m, persistedBuyer.ReservedBalance);
        Assert.Equal(currentCycle.Id, persistedMarket.CurrentCycleId);
        Assert.Equal(CycleStatus.Running, persistedCurrentCycle.Status);
        Assert.Null(persistedCurrentCycle.CompletedAt);
        Assert.Equal(4, random.NextDoubleCalls);
        Assert.Equal(companyCashBefore, await context.Companies
            .Where(candidate => candidate.Id == company.Id)
            .Select(candidate => candidate.CashBalance)
            .SingleAsync());
        Assert.Empty(await context.CorporateCashTransactions
            .Where(transaction => transaction.Type == CorporateCashTransactionType.OperatingIncome)
            .ToListAsync());
        Assert.Empty(await context.CorporateCashTransactions
            .Where(transaction => transaction.Type == CorporateCashTransactionType.DividendDeclared)
            .ToListAsync());
        Assert.Empty(await context.MoneyTransactions
            .Where(transaction => transaction.Type == MoneyTransactionType.Dividend)
            .ToListAsync());
        Assert.Empty(await context.DividendPayouts.ToListAsync());
        Assert.Empty(await context.MoneyTransactions.ToListAsync());
        Assert.Empty(await context.ShareTransactions.ToListAsync());
        Assert.Empty(await context.ParticipantWorthSnapshots.ToListAsync());
        var financialSnapshot = await context.CompanyFinancialSnapshots.SingleAsync();
        Assert.Equal(CompanyFinancialSnapshotMoment.Seed, financialSnapshot.Moment);
    }

    [Fact]
    public async Task PausedBreakFreezesUntilTheRunningLoopResumes()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = await context.Markets.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        var player = await context.Participants.SingleAsync(participant => participant.Name == "Bob");
        var company = await context.Companies.SingleAsync();
        player.Type = ParticipantType.Player;
        player.ReservedBalance = 100m;
        cycle.TradingCycleNumber = 210;
        var day = new TradingDay
        {
            DayNumber = 1,
            State = TradingSessionState.Break,
            OpenedInCycleId = cycle.Id,
            ClosedInCycleId = cycle.Id,
        };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();
        cycle.TradingDayId = day.Id;
        market.CurrentTradingDayId = day.Id;
        market.Status = MarketStatus.Paused;
        var breakCycle = new TradingBreakCycle
        {
            TradingDayId = day.Id,
            StartedAfterCycleId = cycle.Id,
            DurationSeconds = 60,
            IsActive = true,
        };
        var order = new Order
        {
            ParticipantId = player.Id,
            CompanyId = company.Id,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 1,
            LimitPrice = 100m,
            ReservedCashAmount = 100m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.AddRange(breakCycle, order);
        await context.SaveChangesAsync();
        var clock = new TradingClockService(context, Options.Create(new TradingClockOptions()));
        var service = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            tradingClockService: clock);

        Assert.False((await service.RunCycleTickAsync()).Ran);
        Assert.Equal(0, breakCycle.ElapsedSeconds);
        Assert.False((await service.PlaceOrderAsync(player.Id, company.Id, OrderType.Buy, 1, 100m)).Success);
        Assert.True((await service.CancelPlayerOrderAsync(order.Id)).Success);

        await service.SetStatusAsync(MarketStatus.Running);
        var tick = await service.RunCycleTickAsync();

        Assert.True(tick.Ran);
        Assert.Equal(2, breakCycle.ElapsedSeconds);
        Assert.Single(await context.MarketCycles.ToListAsync());
        Assert.Equal(0m, player.ReservedBalance);
        Assert.Equal(OrderStatus.Cancelled, order.Status);

        var standaloneAdvance = await service.AdvanceCycleAsync();

        Assert.True(standaloneAdvance.Success);
        Assert.Equal(4, breakCycle.ElapsedSeconds);
        Assert.Single(await context.TradingBreakCycles.ToListAsync());
        Assert.Single(await context.MarketCycles.ToListAsync());
    }

    [Fact]
    public async Task StandaloneAdvanceProcessesOpeningDayMarginMaintenance()
    {
        var seed = await TestMarketSeed.SeedAccountingScenarioAsync(context);
        var account = new MarginAccount
        {
            ParticipantId = seed.Buyer.Id,
            DebitBalance = 1_000m,
            InitialMarginRate = 0.50m,
            MaintenanceMarginRate = 0.25m,
            Status = MarginAccountStatus.Active,
            LastInterestAccruedTradingDayId = seed.Day.Id + 100,
        };
        context.MarginAccounts.Add(account);
        await context.SaveChangesAsync();
        var margin = new MarginService(context, Options.Create(new MarginOptions { DailyInterestRate = 0.001m }));
        var service = new MarketService(
            context,
            new MatchingEngine(context, marginService: margin),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            marginService: margin);

        await service.AdvanceCycleAsync();

        Assert.Equal(1m, account.AccruedInterest);
        Assert.Equal(seed.Day.Id, account.LastInterestAccruedTradingDayId);
    }

    [Fact]
    public async Task DueSettlementWaitsWhilePausedAndRunsOnTheFirstOpenTick()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = await context.Markets.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        var company = await context.Companies.SingleAsync();
        var seller = await context.Participants.SingleAsync(participant => participant.Name == "Alice");
        var buyer = await context.Participants.SingleAsync(participant => participant.Name == "Bob");
        var sellerHolding = await context.Holdings.SingleAsync();

        var day = new TradingDay { DayNumber = 2, State = TradingSessionState.Trading, OpenedInCycleId = cycle.Id };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();
        cycle.TradingDayId = day.Id;
        cycle.TradingCycleNumber = 1;
        market.CurrentTradingDayId = day.Id;
        market.Status = MarketStatus.Paused;

        seller.CurrentBalance = 1_500m;
        seller.SettledCashBalance = 1_000m;
        buyer.CurrentBalance = 4_500m;
        buyer.SettledCashBalance = 5_000m;
        sellerHolding.Quantity = 5;
        sellerHolding.SettledQuantity = 10;
        var buyerHolding = new Holding
        {
            ParticipantId = buyer.Id,
            CompanyId = company.Id,
            Quantity = 5,
            AverageCost = 100m,
        };
        context.Holdings.Add(buyerHolding);
        var transaction = new ShareTransaction
        {
            SellerId = seller.Id,
            BuyerId = buyer.Id,
            CompanyId = company.Id,
            Quantity = 5,
            Price = 100m,
            TotalCost = 500m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.SettlementInstructions.Add(new SettlementInstruction
        {
            ShareTransaction = transaction,
            BuyerId = buyer.Id,
            SellerId = seller.Id,
            CompanyId = company.Id,
            Quantity = 5,
            CashAmount = 500m,
            TradeDayNumber = 1,
            DueDayNumber = 2,
            Status = SettlementStatus.Pending,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var clock = new TradingClockService(context, Options.Create(new TradingClockOptions()));
        var settlement = new SettlementService(context, Options.Create(new SettlementOptions()));
        var service = new MarketService(
            context,
            new MatchingEngine(context, settlementService: settlement),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            tradingClockService: clock,
            settlementService: settlement);

        Assert.False((await service.RunCycleTickAsync()).Ran);
        Assert.Equal(SettlementStatus.Pending, (await context.SettlementInstructions.SingleAsync()).Status);

        await service.SetStatusAsync(MarketStatus.Running);
        var tick = await service.RunCycleTickAsync();

        Assert.True(tick.Ran);
        Assert.Equal(SettlementStatus.Settled, (await context.SettlementInstructions.SingleAsync()).Status);
        Assert.Equal(seller.CurrentBalance, seller.SettledCashBalance);
        Assert.Equal(buyer.CurrentBalance, buyer.SettledCashBalance);
        Assert.Equal(sellerHolding.Quantity, sellerHolding.SettledQuantity);
        Assert.Equal(buyerHolding.Quantity, buyerHolding.SettledQuantity);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed class CountingCorporateCashRoll : Random
    {
        private readonly Queue<double> values = new([0d, 0d, 0d, 1d]);

        public int NextDoubleCalls { get; private set; }

        public override double NextDouble()
        {
            NextDoubleCalls++;
            return values.Dequeue();
        }

        public override int Next(int minValue, int maxValue) => minValue;
    }

    private sealed class ConstantRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public void Clear() => Commands.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailOnceFinancialSnapshotInterceptor : DbCommandInterceptor
    {
        private bool shouldFail = true;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (shouldFail
                && command.CommandText.Contains(
                    "INSERT INTO \"CompanyFinancialSnapshots\"",
                    StringComparison.Ordinal))
            {
                shouldFail = false;
                throw new InvalidOperationException("Injected late financial seed failure.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private CompanyFinancialService FinancialService(Random random)
    {
        var options = new CompanyFinancialOptions();
        return new CompanyFinancialService(
            context,
            Options.Create(options),
            Options.Create(new RandomChanceRatesOptions()),
            Options.Create(new TradingClockOptions()),
            random,
            new CompanyFinancialScorer(Options.Create(options)));
    }

    private sealed class MutableOptions<T>(T value) : IOptions<T>
        where T : class
    {
        public T Value { get; } = value;
    }
}
