using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class MarginServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly MarginService service;

    public MarginServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        service = new MarginService(context, Options.Create(new MarginOptions
        {
            InitialMarginRate = 0.50m,
            MaintenanceMarginRate = 0.25m,
            DailyInterestRate = 0.001m,
            MaintenanceBufferRate = 0.02m,
            ForcedSaleDiscountRate = 0.05m,
        }));
    }

    [Fact]
    public async Task BuyingPowerRequiresHalfOfANewPositionAndCountsReservations()
    {
        var seed = await SeedAsync(cash: 1_000m);

        Assert.Equal(2_000m, await service.GetBuyingPowerAsync(seed.Participant.Id));

        seed.Participant.ReservedBalance = 600m;
        await context.SaveChangesAsync();
        Assert.Equal(1_400m, await service.GetBuyingPowerAsync(seed.Participant.Id));
    }

    [Fact]
    public async Task MarginPurchaseCreatesDebitWithoutCreatingATermLoan()
    {
        var seed = await SeedAsync(cash: 1_000m);
        var account = await service.GetOrCreateAccountAsync(seed.Participant.Id, seed.Day.Id);

        var advance = service.ApplyPurchase(account, seed.Participant, 1_200m, 1_200m, seed.Cycle.Id, DateTime.UtcNow);
        seed.Participant.CurrentBalance -= 1_200m;
        seed.Participant.ReservedBalance -= 1_200m;
        await context.SaveChangesAsync();

        Assert.Equal(200m, advance);
        Assert.Equal(200m, account.DebitBalance);
        Assert.Equal(0m, seed.Participant.CurrentBalance);
        Assert.Empty(await context.Loans.ToListAsync());
    }

    [Fact]
    public async Task InterestAccruesOnlyOncePerTradingDay()
    {
        var seed = await SeedAsync(cash: 0m);
        var account = await service.GetOrCreateAccountAsync(seed.Participant.Id, seed.Day.Id);
        account.DebitBalance = 10_000m;
        account.LastInterestAccruedTradingDayId = seed.Day.Id;
        var day2 = new TradingDay { DayNumber = 2, State = TradingSessionState.Trading, OpenedInCycleId = seed.Cycle.Id };
        context.TradingDays.Add(day2);
        await context.SaveChangesAsync();

        await service.ProcessForTradingDayAsync(day2.Id, seed.Cycle.Id, DateTime.UtcNow);
        await service.ProcessForTradingDayAsync(day2.Id, seed.Cycle.Id, DateTime.UtcNow);

        Assert.Equal(10m, account.AccruedInterest);
        Assert.Equal(day2.Id, account.LastInterestAccruedTradingDayId);
    }

    [Fact]
    public async Task SalePaysInterestThenDebitBeforeFreeCash()
    {
        var seed = await SeedAsync(cash: 0m);
        var account = await service.GetOrCreateAccountAsync(seed.Participant.Id, seed.Day.Id);
        account.AccruedInterest = 20m;
        account.DebitBalance = 100m;

        var allocation = service.ApplySaleProceeds(account, seed.Participant, 150m, seed.Cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(20m, allocation.InterestPaid);
        Assert.Equal(100m, allocation.DebitPaid);
        Assert.Equal(30m, seed.Participant.CurrentBalance);
        Assert.Equal(0m, account.TotalLiability);
    }

    [Fact]
    public async Task ReadOnlyMetricsAreBuiltForParticipantsInOneBatch()
    {
        var seed = await SeedAsync(cash: 1_000m);
        var second = new Participant
        {
            Name = "Second",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 500m,
            CurrentBalance = 500m,
            SettledCashBalance = 500m,
            IsActive = true,
        };
        context.Participants.Add(second);
        await context.SaveChangesAsync();
        var account = await service.GetOrCreateAccountAsync(second.Id, seed.Day.Id);
        account.DebitBalance = 100m;
        await context.SaveChangesAsync();

        var metrics = await service.GetReadOnlyMetricsByParticipantAsync(
            [seed.Participant, second],
            new Dictionary<int, decimal> { [seed.Participant.Id] = 200m, [second.Id] = 300m });

        Assert.Equal(2, metrics.Count);
        Assert.Equal(1_200m, metrics[seed.Participant.Id].AccountEquity);
        Assert.Equal(700m, metrics[second.Id].AccountEquity);
    }

    [Fact]
    public async Task MaintenanceCallListsOnlyTheSharesNeededForTheBufferedTarget()
    {
        var seed = await SeedAsync(cash: 0m, shares: 100, price: 60m, participantType: ParticipantType.Player);
        var account = await service.GetOrCreateAccountAsync(seed.Participant.Id, seed.Day.Id);
        account.DebitBalance = 5_000m;
        account.LastInterestAccruedTradingDayId = seed.Day.Id;
        await context.SaveChangesAsync();

        await service.ProcessForTradingDayAsync(seed.Day.Id, seed.Cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var call = await context.MarginCalls.SingleAsync();
        Assert.Equal(MarginCallStatus.Open, call.Status);
        var order = await context.Orders.SingleAsync(candidate => candidate.RelatedMarginCallId == call.Id);
        Assert.InRange(order.Quantity, 38, 39);
        Assert.Equal(OrderType.Sell, order.Type);
    }

    [Fact]
    public async Task MaintenanceCallDoesNotRelistSharesAlreadyOfferedForSale()
    {
        var seed = await SeedAsync(cash: 0m, shares: 10, price: 60m, participantType: ParticipantType.Player);
        var account = await service.GetOrCreateAccountAsync(seed.Participant.Id, seed.Day.Id);
        account.DebitBalance = 5_000m;
        account.LastInterestAccruedTradingDayId = seed.Day.Id;
        context.Orders.Add(new Order
        {
            ParticipantId = seed.Participant.Id,
            CompanyId = seed.Company.Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = 10,
            LimitPrice = 60m,
            CreatedInCycleId = seed.Cycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        await service.ProcessForTradingDayAsync(seed.Day.Id, seed.Cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var call = await context.MarginCalls.SingleAsync();
        Assert.False(await context.Orders.AnyAsync(order => order.RelatedMarginCallId == call.Id));
        Assert.Equal(10, await context.Orders
            .Where(order => order.ParticipantId == seed.Participant.Id && order.Type == OrderType.Sell)
            .SumAsync(order => order.Quantity));
    }

    private async Task<(Participant Participant, Company Company, TradingDay Day, MarketCycle Cycle)> SeedAsync(
        decimal cash,
        int shares = 0,
        decimal price = 100m,
        ParticipantType participantType = ParticipantType.Individual)
    {
        var now = DateTime.UtcNow;
        var day = new TradingDay { DayNumber = 1, State = TradingSessionState.Trading, OpenedInCycleId = 0 };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();
        var cycle = new MarketCycle { CycleNumber = 1, TradingDayId = day.Id, TradingCycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);
        var participant = new Participant
        {
            Name = "Trader", Type = participantType, Temperament = Temperament.Balanced, RiskProfile = RiskProfile.Medium,
            InitialBalance = cash, CurrentBalance = cash, SettledCashBalance = cash, IsActive = true,
        };
        var company = new Company { Name = "Acme", IssuedSharesCount = Math.Max(100, shares), CreatedAt = now, UpdatedAt = now };
        context.AddRange(participant, company);
        await context.SaveChangesAsync();
        day.OpenedInCycleId = cycle.Id;
        if (shares > 0)
        {
            context.Holdings.Add(new Holding { ParticipantId = participant.Id, CompanyId = company.Id, Quantity = shares, SettledQuantity = shares, AverageCost = price });
        }
        context.PriceSnapshots.Add(new PriceSnapshot { CompanyId = company.Id, Price = price, CreatedInCycleId = cycle.Id, CreatedAt = now });
        await context.SaveChangesAsync();
        return (participant, company, day, cycle);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
