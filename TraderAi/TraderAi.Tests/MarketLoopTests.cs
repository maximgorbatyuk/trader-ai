using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

        var tick = await marketService.RunCycleTickAsync();

        Assert.True(tick.Ran);
        Assert.Equal(2, tick.OrdersPlaced);
        Assert.Equal(1, tick.FillCount);
        Assert.Equal(1, tick.CompletedCycleNumber);
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
    public async Task ManualStepRunsEvenWhenMarketIsPaused()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        await marketService.SetStatusAsync(MarketStatus.Paused);

        var tick = await marketService.StepCycleAsync();

        Assert.True(tick.Ran);
        Assert.Equal(2, tick.OrdersPlaced);
        Assert.Equal(1, tick.FillCount);
    }

    [Fact]
    public async Task SetStatusReturnsNullWhenNoMarketExists()
    {
        Assert.Null(await marketService.SetStatusAsync(MarketStatus.Paused));
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
        var companyCashBefore = company.CashBalance;
        var random = new CountingCorporateCashRoll();

        var service = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            random);

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
    }

    [Fact]
    public async Task PausedBreakFreezesAutomaticallyButManualStepAdvancesOnlyBreakTime()
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

        var step = await service.StepCycleAsync();

        Assert.True(step.Ran);
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

        var step = await service.StepCycleAsync();

        Assert.True(step.Ran);
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
}
