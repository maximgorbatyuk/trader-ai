using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the real maintain-decide-advance tick with a no-op engine so only hand-placed orders age, and a
// scripted Random so the random reprice roll is deterministic.
public sealed class OrderMaintenanceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public OrderMaintenanceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private MarketService Service(Random random) =>
        new(context, new MatchingEngine(context), new NoOpDecisionEngine(), new MarketCycleLock(), random);

    [Fact]
    public async Task UnfilledBuyOrderIsCancelledAndItsCashReleasedAtTheFiveCycleCap()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var bob = await context.Participants.FirstAsync(participant => participant.Name == "Bob");
        var company = await context.Companies.FirstAsync();
        var market = Service(new NeverReprice());

        var placed = await market.PlaceOrderAsync(bob.Id, company.Id, OrderType.Buy, 2, 110m);
        await StepAsync(market, 6);

        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(OrderStatus.Cancelled, order!.Status);
        Assert.Equal(0m, order.ReservedCashAmount);

        await context.Entry(bob).ReloadAsync();
        Assert.Equal(0m, bob.ReservedBalance);
        Assert.True(await context.MoneyTransactions.AnyAsync(money =>
            money.RelatedOrderId == order.Id && money.Type == MoneyTransactionType.Release));
    }

    [Fact]
    public async Task StaleSellOrderIsRepricedFivePercentTowardTheMarket()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var alice = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var company = await context.Companies.FirstAsync();
        var market = Service(new AlwaysReprice());

        var placed = await market.PlaceOrderAsync(alice.Id, company.Id, OrderType.Sell, 2, 100m);

        // Four ticks brings the order to age 3, where the first reprice fires; still under the cancel cap.
        await StepAsync(market, 4);

        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(OrderStatus.Open, order!.Status);
        Assert.Equal(95m, order.LimitPrice);
    }

    [Fact]
    public async Task RepricedOrderStillCancelsAtTheFiveCycleCap()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var alice = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var company = await context.Companies.FirstAsync();
        var market = Service(new AlwaysReprice());

        var placed = await market.PlaceOrderAsync(alice.Id, company.Id, OrderType.Sell, 2, 100m);
        await StepAsync(market, 6);

        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(OrderStatus.Cancelled, order!.Status);

        // The shares it held are released, so none remain attached to any order.
        Assert.Equal(0, await context.OrderShares.CountAsync());
    }

    [Fact]
    public async Task CashStarvedHolderLiquidatesHalfOfItsPriciestHolding()
    {
        await SeedStarvedHolderAsync(cash: 10m, cheapPrice: 50m, dearPrice: 200m, sharesEach: 4);
        var poor = await context.Participants.FirstAsync(participant => participant.Name == "Poor");
        var dear = await context.Companies.FirstAsync(company => company.Name == "Dear Co");
        var market = Service(new AlwaysReprice());

        await StepAsync(market, 5);

        var liquidation = await context.Orders.SingleAsync(order =>
            order.ParticipantId == poor.Id && order.Type == OrderType.Sell);
        Assert.Equal(dear.Id, liquidation.CompanyId);
        Assert.Equal(2, liquidation.Quantity);

        await context.Entry(poor).ReloadAsync();
        Assert.Equal(0, poor.CashStarvedCycles);
    }

    private static async Task StepAsync(MarketService market, int times)
    {
        for (var step = 0; step < times; step++)
        {
            await market.StepCycleAsync();
        }
    }

    private async Task SeedStarvedHolderAsync(decimal cash, decimal cheapPrice, decimal dearPrice, int sharesEach)
    {
        var now = DateTime.UtcNow;

        var cycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);

        var market = new Market { Name = "Test Market", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);

        var cheap = new Company { Name = "Cheap Co", IssuedSharesCount = sharesEach, CreatedAt = now, UpdatedAt = now };
        var dear = new Company { Name = "Dear Co", IssuedSharesCount = sharesEach, CreatedAt = now, UpdatedAt = now };
        context.Companies.Add(cheap);
        context.Companies.Add(dear);

        var poor = new Participant
        {
            Name = "Poor",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = cash,
            CurrentBalance = cash,
            IsActive = true,
        };
        context.Participants.Add(poor);
        await context.SaveChangesAsync();

        foreach (var (company, price) in new[] { (cheap, cheapPrice), (dear, dearPrice) })
        {
            for (var index = 0; index < sharesEach; index++)
            {
                context.Shares.Add(new Share
                {
                    CompanyId = company.Id,
                    OwnerId = poor.Id,
                    InitialPrice = price,
                    CurrentPrice = price,
                    LastUpdatedAt = now,
                });
            }

            context.PriceSnapshots.Add(new PriceSnapshot
            {
                CompanyId = company.Id,
                Price = price,
                CreatedInCycleId = cycle.Id,
                CreatedAt = now,
            });
        }

        market.CurrentCycleId = cycle.Id;
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed class AlwaysReprice : Random
    {
        public override double NextDouble() => 0d;
    }

    private sealed class NeverReprice : Random
    {
        public override double NextDouble() => 0.99d;
    }
}
