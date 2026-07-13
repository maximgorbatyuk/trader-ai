using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AccountingReconciliationTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public AccountingReconciliationTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task PrimaryIssuanceOperatingIncomeAndDividendReconcile()
    {
        var seed = await TestMarketSeed.SeedAccountingScenarioAsync(context);
        var settlement = Settlement();
        var initialCash = await ConservedCashAsync();
        await MatchPrimaryAsync(seed, settlement, quantity: 10, price: 100m);

        Assert.Equal(initialCash, await ConservedCashAsync());
        await AssertPendingDeltasReconcileAsync();
        Assert.Equal(0m, seed.Company.CashBalance);

        var dayTwoCycle = await OpenNextDayAsync(seed);
        Assert.Equal(1, await settlement.SettleDueAsync(2, dayTwoCycle.Id, DateTime.UtcNow));
        await context.SaveChangesAsync();

        Assert.Equal(initialCash, await ConservedCashAsync());
        Assert.Equal(1_000m, seed.Company.CashBalance);
        await AssertPendingDeltasReconcileAsync();

        seed.Market.NextDividendCycleNumber = dayTwoCycle.CycleNumber;
        await context.SaveChangesAsync();
        var service = new MarketService(
            context,
            new MatchingEngine(context, settlementService: settlement),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new FloorRoll(),
            settlementService: settlement);
        await service.StepCycleAsync();

        var dividendDebit = await context.CorporateCashTransactions
            .Where(transaction => transaction.Type == CorporateCashTransactionType.DividendDeclared)
            .SumAsync(transaction => transaction.Amount);
        var primaryIssuanceCredits = await context.CorporateCashTransactions
            .Where(transaction => transaction.Type == CorporateCashTransactionType.PrimaryIssuance)
            .SumAsync(transaction => transaction.Amount);
        var operatingIncomeCredits = await context.CorporateCashTransactions
            .Where(transaction => transaction.Type == CorporateCashTransactionType.OperatingIncome)
            .SumAsync(transaction => transaction.Amount);
        var participantCredits = await context.MoneyTransactions
            .Where(transaction => transaction.Type == MoneyTransactionType.Dividend)
            .SumAsync(transaction => transaction.Amount);
        var payoutLines = await context.DividendPayouts.SumAsync(payout => payout.Amount);
        Assert.True(operatingIncomeCredits > 0m);
        Assert.True(dividendDebit > 0m);
        Assert.Equal(
            primaryIssuanceCredits + operatingIncomeCredits - dividendDebit,
            seed.Company.CashBalance);
        Assert.Equal(dividendDebit, participantCredits);
        Assert.Equal(dividendDebit, payoutLines);
        Assert.Equal(initialCash, await ConservedCashAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CorporateCashLedgerRejectsNonPositiveAmounts(int amount)
    {
        var seed = await TestMarketSeed.SeedAccountingScenarioAsync(context);
        context.CorporateCashTransactions.Add(new CorporateCashTransaction
        {
            CompanyId = seed.Company.Id,
            Type = CorporateCashTransactionType.PrimaryIssuance,
            Amount = amount,
            CreatedInCycleId = seed.Cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SecondarySaleFeeAndSettlementReconcile()
    {
        var seed = await TestMarketSeed.SeedAccountingScenarioAsync(context);
        var settlement = Settlement();
        var initialCash = await ConservedCashAsync();
        seed.Buyer.ReservedBalance = 500m;
        context.Orders.AddRange(
            Buy(seed, 5, 100m),
            Sell(seed, 5, 100m));
        await context.SaveChangesAsync();
        var engine = new MatchingEngine(
            context,
            Options.Create(new TradeFeeOptions { Enabled = true, FeeRate = 0.01m }),
            settlement);

        Assert.Equal(1, await engine.RunAsync(seed.Cycle));
        await context.SaveChangesAsync();

        Assert.Equal(4_500m, seed.Buyer.CurrentBalance);
        Assert.Equal(1_495m, seed.Seller.CurrentBalance);
        Assert.Equal(5m, seed.Bank.Balance);
        Assert.Equal(initialCash, await ConservedCashAsync());
        await AssertPendingDeltasReconcileAsync();

        var dayTwoCycle = await OpenNextDayAsync(seed);
        Assert.Equal(1, await settlement.SettleDueAsync(2, dayTwoCycle.Id, DateTime.UtcNow));
        await context.SaveChangesAsync();

        Assert.Equal(seed.Buyer.CurrentBalance, seed.Buyer.SettledCashBalance);
        Assert.Equal(seed.Seller.CurrentBalance, seed.Seller.SettledCashBalance);
        Assert.Equal(initialCash, await ConservedCashAsync());
        await AssertPendingDeltasReconcileAsync();
    }

    [Fact]
    public async Task ExplicitLoanAndMarginDebitRemainSeparateLiabilities()
    {
        var seed = await TestMarketSeed.SeedAccountingScenarioAsync(context);
        var loanService = new LoanService(context, Options.Create(new LoanOptions { Enabled = true }));
        var initialCash = await ConservedCashAsync();
        var loan = await loanService.OriginateLoanAsync(
            seed.Buyer,
            principal: 300m,
            grossWorth: 5_000m,
            seed.Cycle.Id,
            DateTime.UtcNow);
        var margin = new MarginAccount
        {
            ParticipantId = seed.Buyer.Id,
            DebitBalance = 200m,
            InitialMarginRate = 0.50m,
            MaintenanceMarginRate = 0.25m,
            Status = MarginAccountStatus.Active,
            LastInterestAccruedTradingDayId = seed.Day.Id,
        };
        seed.Buyer.CurrentBalance += 200m;
        seed.Buyer.SettledCashBalance += 200m;
        context.MarginAccounts.Add(margin);
        await context.SaveChangesAsync();

        Assert.NotNull(loan);
        Assert.Single(await context.Loans.ToListAsync());
        Assert.Single(await context.MarginAccounts.ToListAsync());
        Assert.Equal(300m, loan!.TotalLiability);
        Assert.Equal(200m, margin.TotalLiability);
        Assert.Equal(initialCash, await ConservedCashAsync());

        var netWorth = seed.Buyer.CurrentBalance - loan.TotalLiability - margin.TotalLiability;
        Assert.Equal(5_000m, netWorth);
    }

    [Fact]
    public async Task MarginDeclineCallForcedSaleAndSettlementReconcile()
    {
        var seed = await TestMarketSeed.SeedAccountingScenarioAsync(context);
        var settlement = Settlement();
        var margin = new MarginService(context, Options.Create(new MarginOptions()));
        var initialCash = await ConservedCashAsync();
        seed.Buyer.ReservedBalance = 6_000m;
        context.Orders.AddRange(
            Buy(seed, 60, 100m),
            new Order
            {
                ParticipantId = null,
                CompanyId = seed.Company.Id,
                Type = OrderType.Sell,
                Status = OrderStatus.Open,
                Quantity = 60,
                LimitPrice = 100m,
                CreatedInCycleId = seed.Cycle.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await context.SaveChangesAsync();
        var engine = new MatchingEngine(context, settlementService: settlement, marginService: margin);
        Assert.Equal(1, await engine.RunAsync(seed.Cycle));
        await context.SaveChangesAsync();

        Assert.Equal(1_000m, await context.MarginAccounts
            .Where(account => account.ParticipantId == seed.Buyer.Id)
            .Select(account => account.DebitBalance)
            .SingleAsync());
        Assert.Empty(await context.Loans.ToListAsync());
        Assert.Equal(initialCash, await ConservedCashAsync());
        await AssertPendingDeltasReconcileAsync();

        var dayTwoCycle = await OpenNextDayAsync(seed);
        await settlement.SettleDueAsync(2, dayTwoCycle.Id, DateTime.UtcNow);
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = seed.Company.Id,
            Price = 10m,
            Capitalization = 1_000m,
            CreatedInCycleId = dayTwoCycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        await margin.ProcessForTradingDayAsync(seed.Market.CurrentTradingDayId!.Value, dayTwoCycle.Id, DateTime.UtcNow);

        var call = await context.MarginCalls.SingleAsync(candidate => candidate.Status == MarginCallStatus.Open);
        var forcedSale = await context.Orders.SingleAsync(order => order.RelatedMarginCallId == call.Id);
        Assert.InRange(forcedSale.Quantity, 1, 60);
        seed.Seller.ReservedBalance = forcedSale.Quantity * 10m;
        context.Orders.Add(new Order
        {
            ParticipantId = seed.Seller.Id,
            CompanyId = seed.Company.Id,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = forcedSale.Quantity,
            LimitPrice = 10m,
            ReservedCashAmount = forcedSale.Quantity * 10m,
            CreatedInCycleId = dayTwoCycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        Assert.Equal(1, await engine.RunAsync(dayTwoCycle));
        await context.SaveChangesAsync();

        Assert.Equal(initialCash, await ConservedCashAsync());
        await AssertPendingDeltasReconcileAsync();
        var debitAfterSale = await context.MarginAccounts
            .Where(account => account.ParticipantId == seed.Buyer.Id)
            .Select(account => account.DebitBalance)
            .SingleAsync();
        Assert.InRange(debitAfterSale, 0m, 999.99m);

        await settlement.SettleDueAsync(3, dayTwoCycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();
        Assert.Equal(initialCash, await ConservedCashAsync());
        await AssertPendingDeltasReconcileAsync();
    }

    [Fact]
    public async Task FailureAfterDueSettlementStagingRollsBackAccountingState()
    {
        var seed = await TestMarketSeed.SeedAccountingScenarioAsync(context);
        var settlement = Settlement();
        await MatchPrimaryAsync(seed, settlement, quantity: 10, price: 100m);
        var dayTwoCycle = await OpenNextDayAsync(seed);
        seed.Market.NextDividendCycleNumber = 100;
        context.MarketCycles.Add(new MarketCycle
        {
            CycleNumber = dayTwoCycle.CycleNumber + 1,
            TradingDayId = seed.Day.Id,
            TradingCycleNumber = 2,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        var instructionId = await context.SettlementInstructions.Select(instruction => instruction.Id).SingleAsync();
        var settledCashBefore = seed.Buyer.SettledCashBalance;
        var settledQuantityBefore = await context.Holdings
            .Where(holding => holding.ParticipantId == seed.Buyer.Id && holding.CompanyId == seed.Company.Id)
            .Select(holding => holding.SettledQuantity)
            .SingleAsync();
        var service = new MarketService(
            context,
            new MatchingEngine(context, settlementService: settlement),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            settlementService: settlement);

        await Assert.ThrowsAsync<DbUpdateException>(() => service.RunCycleTickAsync());

        context.ChangeTracker.Clear();
        var instruction = await context.SettlementInstructions.SingleAsync(candidate => candidate.Id == instructionId);
        Assert.Equal(SettlementStatus.Pending, instruction.Status);
        Assert.Null(instruction.SettledAt);
        Assert.Equal(0m, await context.Companies.Select(company => company.CashBalance).SingleAsync());
        Assert.Equal(settledCashBefore, await context.Participants
            .Where(participant => participant.Id == seed.Buyer.Id)
            .Select(participant => participant.SettledCashBalance)
            .SingleAsync());
        Assert.Equal(settledQuantityBefore, await context.Holdings
            .Where(holding => holding.ParticipantId == seed.Buyer.Id && holding.CompanyId == seed.Company.Id)
            .Select(holding => holding.SettledQuantity)
            .SingleAsync());
    }

    private SettlementService Settlement() =>
        new(context, Options.Create(new SettlementOptions { SettlementLagTradingDays = 1 }));

    private async Task MatchPrimaryAsync(AccountingMarketSeed seed, SettlementService settlement, int quantity, decimal price)
    {
        seed.Buyer.ReservedBalance = quantity * price;
        context.Orders.AddRange(
            Buy(seed, quantity, price),
            new Order
            {
                ParticipantId = null,
                CompanyId = seed.Company.Id,
                Type = OrderType.Sell,
                Status = OrderStatus.Open,
                Quantity = quantity,
                LimitPrice = price,
                CreatedInCycleId = seed.Cycle.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await context.SaveChangesAsync();
        Assert.Equal(1, await new MatchingEngine(context, settlementService: settlement).RunAsync(seed.Cycle));
        await context.SaveChangesAsync();
    }

    private static Order Buy(AccountingMarketSeed seed, int quantity, decimal price) => new()
    {
        ParticipantId = seed.Buyer.Id,
        CompanyId = seed.Company.Id,
        Type = OrderType.Buy,
        Status = OrderStatus.Open,
        Quantity = quantity,
        LimitPrice = price,
        ReservedCashAmount = quantity * price,
        CreatedInCycleId = seed.Cycle.Id,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Order Sell(AccountingMarketSeed seed, int quantity, decimal price) => new()
    {
        ParticipantId = seed.Seller.Id,
        CompanyId = seed.Company.Id,
        Type = OrderType.Sell,
        Status = OrderStatus.Open,
        Quantity = quantity,
        LimitPrice = price,
        CreatedInCycleId = seed.Cycle.Id,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private async Task<MarketCycle> OpenNextDayAsync(AccountingMarketSeed seed)
    {
        seed.Day.State = TradingSessionState.Break;
        seed.Day.ClosedInCycleId = seed.Cycle.Id;
        var day = new TradingDay { DayNumber = 2, State = TradingSessionState.Trading, OpenedInCycleId = 0 };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();
        var cycle = new MarketCycle
        {
            CycleNumber = seed.Cycle.CycleNumber + 1,
            TradingDayId = day.Id,
            TradingCycleNumber = 1,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();
        day.OpenedInCycleId = cycle.Id;
        seed.Market.CurrentCycleId = cycle.Id;
        seed.Market.CurrentTradingDayId = day.Id;
        await context.SaveChangesAsync();
        return cycle;
    }

    private async Task<decimal> ConservedCashAsync()
    {
        var participantCash = await context.Participants.SumAsync(participant => participant.CurrentBalance);
        var issuerCash = await context.Companies.SumAsync(company => company.CashBalance);
        var bankCash = await context.Banks.SumAsync(bank => bank.Balance);
        var pendingPrimary = await context.SettlementInstructions
            .Where(instruction => instruction.Status == SettlementStatus.Pending && instruction.SellerId == null)
            .SumAsync(instruction => instruction.CashAmount);
        var loanPrincipal = await context.Loans
            .Where(loan => loan.Status == LoanStatus.Open)
            .SumAsync(loan => loan.RemainingPrincipal);
        var marginDebit = await context.MarginAccounts.SumAsync(account => account.DebitBalance);
        var externalOperatingIncome = await context.CorporateCashTransactions
            .Where(transaction => transaction.Type == CorporateCashTransactionType.OperatingIncome)
            .SumAsync(transaction => transaction.Amount);
        return participantCash
            + issuerCash
            + bankCash
            + pendingPrimary
            - loanPrincipal
            - marginDebit
            - externalOperatingIncome;
    }

    private async Task AssertPendingDeltasReconcileAsync()
    {
        var participants = await context.Participants.ToListAsync();
        var pending = await context.SettlementInstructions
            .Where(instruction => instruction.Status == SettlementStatus.Pending)
            .ToListAsync();
        foreach (var participant in participants)
        {
            var pendingCash = pending.Sum(instruction =>
                instruction.BuyerId == participant.Id
                    ? -instruction.CashAmount
                    : instruction.SellerId == participant.Id ? instruction.CashAmount : 0m);
            Assert.Equal(pendingCash, participant.CurrentBalance - participant.SettledCashBalance);
        }

        foreach (var holding in await context.Holdings.ToListAsync())
        {
            var pendingQuantity = pending
                .Where(instruction => instruction.CompanyId == holding.CompanyId)
                .Sum(instruction => instruction.BuyerId == holding.ParticipantId
                    ? instruction.Quantity
                    : instruction.SellerId == holding.ParticipantId ? -instruction.Quantity : 0);
            Assert.Equal(pendingQuantity, holding.Quantity - holding.SettledQuantity);
        }
    }

    private sealed class FloorRoll : Random
    {
        public override double NextDouble() => 0d;

        public override int Next(int minValue, int maxValue) => minValue;
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
