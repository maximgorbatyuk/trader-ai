using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class SettlementServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public SettlementServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task FillMovesEconomicBalancesButLeavesSettlementPendingUntilNextDay()
    {
        var seed = await SeedAsync();
        var settlement = Service();
        await new MatchingEngine(context, settlementService: settlement).RunAsync(seed.Cycle);
        await context.SaveChangesAsync();

        Assert.Equal(4_500m, seed.Buyer.CurrentBalance);
        Assert.Equal(5_000m, seed.Buyer.SettledCashBalance);
        Assert.Equal(1_500m, seed.Seller.CurrentBalance);
        Assert.Equal(1_000m, seed.Seller.SettledCashBalance);

        var buyerHolding = await context.Holdings.SingleAsync(holding => holding.ParticipantId == seed.Buyer.Id);
        var sellerHolding = await context.Holdings.SingleAsync(holding => holding.ParticipantId == seed.Seller.Id);
        Assert.Equal(5, buyerHolding.Quantity);
        Assert.Equal(0, buyerHolding.SettledQuantity);
        Assert.Equal(5, sellerHolding.Quantity);
        Assert.Equal(10, sellerHolding.SettledQuantity);

        var instruction = await context.SettlementInstructions.SingleAsync();
        Assert.Equal(SettlementStatus.Pending, instruction.Status);
        Assert.Equal(1, instruction.TradeDayNumber);
        Assert.Equal(2, instruction.DueDayNumber);
        Assert.Equal(500m, instruction.CashAmount);
        Assert.Equal(5, instruction.Quantity);
        Assert.Equal(0, await settlement.SettleDueAsync(1, seed.Cycle.Id, DateTime.UtcNow));
    }

    [Fact]
    public async Task DueBatchNetsBothLegsAndSettlesExactlyOnce()
    {
        var seed = await SeedAsync();
        var settlement = Service();
        await new MatchingEngine(context, settlementService: settlement).RunAsync(seed.Cycle);
        await context.SaveChangesAsync();
        var dayTwoCycle = await AddDayAsync(2, 2);

        Assert.Equal(1, await settlement.SettleDueAsync(2, dayTwoCycle.Id, DateTime.UtcNow));
        Assert.Equal(0, await settlement.SettleDueAsync(2, dayTwoCycle.Id, DateTime.UtcNow));
        await context.SaveChangesAsync();
        Assert.Equal(0, await settlement.SettleDueAsync(2, dayTwoCycle.Id, DateTime.UtcNow));

        Assert.Equal(seed.Buyer.CurrentBalance, seed.Buyer.SettledCashBalance);
        Assert.Equal(seed.Seller.CurrentBalance, seed.Seller.SettledCashBalance);
        Assert.All(await context.Holdings.ToListAsync(), holding => Assert.Equal(holding.Quantity, holding.SettledQuantity));
        var instruction = await context.SettlementInstructions.SingleAsync();
        Assert.Equal(SettlementStatus.Settled, instruction.Status);
        Assert.Equal(dayTwoCycle.Id, instruction.SettledInCycleId);
    }

    [Fact]
    public async Task MarginAdvanceAndSaleRepaymentKeepEconomicAndSettledCashReconciled()
    {
        var seed = await SeedAsync();
        seed.Buyer.CurrentBalance = 400m;
        seed.Buyer.SettledCashBalance = 400m;
        var margin = new MarginService(context, Options.Create(new MarginOptions()));
        var sellerAccount = await margin.GetOrCreateAccountAsync(seed.Seller.Id, seed.Day.Id);
        sellerAccount.AccruedInterest = 20m;
        sellerAccount.DebitBalance = 100m;
        await context.SaveChangesAsync();
        var settlement = Service();

        await new MatchingEngine(context, settlementService: settlement, marginService: margin).RunAsync(seed.Cycle);
        await context.SaveChangesAsync();

        var instruction = await context.SettlementInstructions.SingleAsync();
        Assert.Equal(100m, instruction.BuyerMarginAdvance);
        Assert.Equal(20m, instruction.SellerMarginInterestPayment);
        Assert.Equal(100m, instruction.SellerMarginDebitRepayment);
        Assert.Equal(-500m, seed.Buyer.CurrentBalance - seed.Buyer.SettledCashBalance);
        Assert.Equal(500m, seed.Seller.CurrentBalance - seed.Seller.SettledCashBalance);

        var dayTwoCycle = await AddDayAsync(2, 2);
        await settlement.SettleDueAsync(2, dayTwoCycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(seed.Buyer.CurrentBalance, seed.Buyer.SettledCashBalance);
        Assert.Equal(seed.Seller.CurrentBalance, seed.Seller.SettledCashBalance);
        Assert.Equal(0m, sellerAccount.TotalLiability);
        Assert.Empty(await context.Loans.ToListAsync());
    }

    [Fact]
    public async Task SettlementNetsSameDayBuyAndResaleBeforeApplyingSettledQuantity()
    {
        var seed = await SeedAsync(sellerShares: 5);
        var reseller = await AddParticipantAsync("Reseller", 1_000m);
        var secondBuyer = await AddParticipantAsync("Second buyer", 1_000m);
        context.Holdings.Remove(await context.Holdings.SingleAsync());
        context.Orders.RemoveRange(await context.Orders.ToListAsync());
        var now = DateTime.UtcNow;
        context.Orders.AddRange(
            Order(null, seed.Company.Id, OrderType.Sell, 5, now.AddSeconds(-3)),
            Order(reseller.Id, seed.Company.Id, OrderType.Buy, 5, now.AddSeconds(-2), reserved: 500m),
            Order(reseller.Id, seed.Company.Id, OrderType.Sell, 5, now.AddSeconds(-1)),
            Order(secondBuyer.Id, seed.Company.Id, OrderType.Buy, 5, now, reserved: 500m));
        reseller.ReservedBalance = 500m;
        secondBuyer.ReservedBalance = 500m;
        await context.SaveChangesAsync();

        var settlement = Service();
        Assert.Equal(2, await new MatchingEngine(context, settlementService: settlement).RunAsync(seed.Cycle));
        await context.SaveChangesAsync();
        var resellerHolding = await context.Holdings.SingleAsync(holding => holding.ParticipantId == reseller.Id);
        Assert.Equal(0, resellerHolding.Quantity);
        Assert.Equal(0, resellerHolding.SettledQuantity);

        var dayTwoCycle = await AddDayAsync(2, 2);
        Assert.Equal(2, await settlement.SettleDueAsync(2, dayTwoCycle.Id, DateTime.UtcNow));
        await context.SaveChangesAsync();

        Assert.Equal(0, resellerHolding.SettledQuantity);
        Assert.Equal(reseller.CurrentBalance, reseller.SettledCashBalance);
        Assert.Equal(5, (await context.Holdings.SingleAsync(holding => holding.ParticipantId == secondBuyer.Id)).SettledQuantity);
    }

    [Fact]
    public async Task IssuerProceedsAndLedgerMoveOnlyWhenPrimaryFillSettles()
    {
        var seed = await SeedAsync(sellerShares: 0);
        context.Orders.RemoveRange(await context.Orders.ToListAsync());
        var now = DateTime.UtcNow;
        context.Orders.AddRange(
            Order(null, seed.Company.Id, OrderType.Sell, 5, now.AddSeconds(-1)),
            Order(seed.Buyer.Id, seed.Company.Id, OrderType.Buy, 5, now, reserved: 500m));
        seed.Buyer.ReservedBalance = 500m;
        await context.SaveChangesAsync();

        var settlement = Service();
        await new MatchingEngine(context, settlementService: settlement).RunAsync(seed.Cycle);
        await context.SaveChangesAsync();
        Assert.Equal(0m, seed.Company.CashBalance);
        Assert.Empty(await context.CorporateCashTransactions.ToListAsync());

        var dayTwoCycle = await AddDayAsync(2, 2);
        Assert.Equal(1, await settlement.SettleDueAsync(2, dayTwoCycle.Id, DateTime.UtcNow));
        await context.SaveChangesAsync();
        Assert.Equal(500m, seed.Company.CashBalance);
        var movement = await context.CorporateCashTransactions.SingleAsync();
        Assert.Equal(CorporateCashTransactionType.PrimaryIssuance, movement.Type);
        Assert.Equal(500m, movement.Amount);
    }

    private SettlementService Service() =>
        new(context, Options.Create(new SettlementOptions { SettlementLagTradingDays = 1 }));

    private async Task<Seed> SeedAsync(int sellerShares = 10)
    {
        var day = new TradingDay { DayNumber = 1, State = TradingSessionState.Trading, OpenedInCycleId = 0 };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();
        var cycle = new MarketCycle
        {
            CycleNumber = 1,
            TradingDayId = day.Id,
            TradingCycleNumber = 1,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        context.MarketCycles.Add(cycle);
        var company = new Company { Name = "Acme", IssuedSharesCount = 10, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        context.Companies.Add(company);
        var seller = await AddParticipantAsync("Seller", 1_000m);
        var buyer = await AddParticipantAsync("Buyer", 5_000m);
        await context.SaveChangesAsync();
        day.OpenedInCycleId = cycle.Id;
        if (sellerShares > 0)
        {
            context.Holdings.Add(new Holding
            {
                ParticipantId = seller.Id,
                CompanyId = company.Id,
                Quantity = sellerShares,
                SettledQuantity = sellerShares,
                AverageCost = 100m,
            });
        }
        var now = DateTime.UtcNow;
        context.Orders.AddRange(
            Order(seller.Id, company.Id, OrderType.Sell, 5, now.AddSeconds(-1)),
            Order(buyer.Id, company.Id, OrderType.Buy, 5, now, reserved: 500m));
        buyer.ReservedBalance = 500m;
        await context.SaveChangesAsync();
        return new Seed(day, cycle, company, seller, buyer);
    }

    private async Task<Participant> AddParticipantAsync(string name, decimal cash)
    {
        var participant = new Participant
        {
            Name = name,
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = cash,
            CurrentBalance = cash,
            SettledCashBalance = cash,
            IsActive = true,
        };
        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        return participant;
    }

    private async Task<MarketCycle> AddDayAsync(int dayNumber, int cycleNumber)
    {
        var day = new TradingDay { DayNumber = dayNumber, State = TradingSessionState.Trading, OpenedInCycleId = 0 };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();
        var cycle = new MarketCycle
        {
            CycleNumber = cycleNumber,
            TradingDayId = day.Id,
            TradingCycleNumber = 1,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();
        day.OpenedInCycleId = cycle.Id;
        await context.SaveChangesAsync();
        return cycle;
    }

    private static Order Order(int? participantId, int companyId, OrderType type, int quantity, DateTime createdAt, decimal reserved = 0m) =>
        new()
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Type = type,
            Status = OrderStatus.Open,
            Quantity = quantity,
            LimitPrice = 100m,
            ReservedCashAmount = reserved,
            CreatedInCycleId = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed record Seed(TradingDay Day, MarketCycle Cycle, Company Company, Participant Seller, Participant Buyer);
}
