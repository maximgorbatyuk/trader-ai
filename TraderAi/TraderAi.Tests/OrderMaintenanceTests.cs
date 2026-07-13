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
    public async Task UnfilledBuyOrderIsCancelledAndItsCashReleasedAtTheAgeCap()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var bob = await context.Participants.FirstAsync(participant => participant.Name == "Bob");
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));

        var placed = await market.PlaceOrderAsync(bob.Id, company.Id, OrderType.Buy, 2, 110m);
        await StepAsync(market, 16);

        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(OrderStatus.Cancelled, order!.Status);
        Assert.Equal(0m, order.ReservedCashAmount);

        await context.Entry(bob).ReloadAsync();
        Assert.Equal(0m, bob.ReservedBalance);
        Assert.True(await context.MoneyTransactions.AnyAsync(money =>
            money.RelatedOrderId == order.Id && money.Type == MoneyTransactionType.Release));
    }

    [Fact]
    public async Task StaleSellOrderIsRepricedTenPercentTowardTheMarket()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var alice = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));

        var placed = await market.PlaceOrderAsync(alice.Id, company.Id, OrderType.Sell, 2, 100m);

        // Four ticks brings the order to age 3, where the first reprice fires; still under the cancel cap.
        await StepAsync(market, 4);

        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(OrderStatus.Open, order!.Status);
        Assert.Equal(90m, order.LimitPrice);
    }

    [Fact]
    public async Task RepricedOrderStillCancelsAtTheAgeCap()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var alice = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));

        var placed = await market.PlaceOrderAsync(alice.Id, company.Id, OrderType.Sell, 2, 100m);
        await StepAsync(market, 16);

        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(OrderStatus.Cancelled, order!.Status);

        // The listing is cancelled, so no open sell order ties up the shares any longer.
        Assert.Equal(0, await context.Orders.CountAsync(sell =>
            sell.Type == OrderType.Sell && (sell.Status == OrderStatus.Open || sell.Status == OrderStatus.PartiallyFilled)));
    }

    [Fact]
    public async Task CashStarvedHolderLiquidatesHalfOfItsPriciestHolding()
    {
        await SeedStarvedHolderAsync(cash: 10m, cheapPrice: 50m, dearPrice: 200m, sharesEach: 4);
        var poor = await context.Participants.FirstAsync(participant => participant.Name == "Poor");
        var dear = await context.Companies.FirstAsync(company => company.Name == "Dear Co");
        var market = Service(new FixedRoll(0d));

        await StepAsync(market, 5);

        var liquidation = await context.Orders.SingleAsync(order =>
            order.ParticipantId == poor.Id && order.Type == OrderType.Sell);
        Assert.Equal(dear.Id, liquidation.CompanyId);
        Assert.Equal(2, liquidation.Quantity);

        await context.Entry(poor).ReloadAsync();
        Assert.Equal(0, poor.CashStarvedCycles);
    }

    [Fact]
    public async Task RepriceChanceRisesFromTheEarlyBandIntoTheMidBand()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var alice = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0.6));

        var placed = await market.PlaceOrderAsync(alice.Id, company.Id, OrderType.Sell, 2, 100m);

        // Ages 3-6 carry a 0.5 chance, so a 0.6 roll never clears it through age 6.
        await StepAsync(market, 7);
        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(100m, order!.LimitPrice);

        // Age 7 enters the 0.7 band, where the same 0.6 roll now reprices.
        await StepAsync(market, 1);
        await context.Entry(order).ReloadAsync();
        Assert.Equal(90m, order.LimitPrice);
    }

    [Fact]
    public async Task RepriceChanceReachesCertaintyInTheLateBand()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var alice = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0.8));

        var placed = await market.PlaceOrderAsync(alice.Id, company.Id, OrderType.Sell, 2, 100m);

        // A 0.8 roll clears neither the 0.5 (ages 3-6) nor the 0.7 (ages 7-12) band, so nothing reprices.
        await StepAsync(market, 13);
        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(100m, order!.LimitPrice);

        // Age 13 enters the certain band, where every roll reprices.
        await StepAsync(market, 1);
        await context.Entry(order).ReloadAsync();
        Assert.Equal(90m, order.LimitPrice);
    }

    [Fact]
    public async Task StaleSellNeverCompoundsBelowTheActiveLowerBand()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var alice = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));

        var placed = await market.PlaceOrderAsync(alice.Id, company.Id, OrderType.Sell, 2, 100m);
        // Old compounding would chase 100 -> 90 -> 81 -> ... toward 60; the band floor now stops it at 85.
        await StepAsync(market, 8);

        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(OrderStatus.Open, order!.Status);
        Assert.Equal(85m, order.LimitPrice);
    }

    [Fact]
    public async Task StaleBuyNeverCompoundsAboveTheActiveUpperBand()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var bob = await context.Participants.FirstAsync(participant => participant.Name == "Bob");
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));

        var placed = await market.PlaceOrderAsync(bob.Id, company.Id, OrderType.Buy, 2, 100m);
        // Old compounding would chase 100 -> 110 -> 121 -> ... toward 180; the band ceiling now stops it at 110.
        await StepAsync(market, 8);

        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(OrderStatus.Open, order!.Status);
        Assert.Equal(110m, order.LimitPrice);
    }

    [Fact]
    public async Task WaitingOuterSellRepricesUpTowardTheActiveBand()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var alice = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));

        // 80 rests below the 85 lower band but inside the 75-115 allowed range.
        var placed = await market.PlaceOrderAsync(alice.Id, company.Id, OrderType.Sell, 2, 80m);
        await StepAsync(market, 4);

        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(OrderStatus.Open, order!.Status);
        Assert.Equal(88m, order.LimitPrice);
    }

    [Fact]
    public async Task ParticipantOrderOutsideTheAllowedRangeIsCancelledAndItsBuyReservationReleasedOnce()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var bob = await context.Participants.FirstAsync(participant => participant.Name == "Bob");
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));

        var placed = await market.PlaceOrderAsync(bob.Id, company.Id, OrderType.Buy, 2, 110m);
        // The band rolls up to a $200 reference, so the old 110 bid now sits below the 150 allowed minimum.
        await SetBandAsync(company.Id, reference: 200m, lower: 170m, upper: 220m);

        await StepAsync(market, 1);

        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(OrderStatus.Cancelled, order!.Status);
        Assert.Equal(0m, order.ReservedCashAmount);
        await context.Entry(bob).ReloadAsync();
        Assert.Equal(0m, bob.ReservedBalance);
        Assert.Equal(1, await context.MoneyTransactions.CountAsync(money =>
            money.RelatedOrderId == order.Id && money.Type == MoneyTransactionType.Release));
    }

    [Fact]
    public async Task PlayerOrderInsideTheAllowedRangeSurvivesBandMovementUnchanged()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;

        var placed = await market.PlaceOrderAsync(player.Id, company.Id, OrderType.Buy, 2, 100m);
        // A $110 reference keeps 100 inside the 82.50-126.50 allowed range.
        await SetBandAsync(company.Id, reference: 110m, lower: 93.5m, upper: 121m);

        await StepAsync(market, 5);

        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(OrderStatus.Open, order!.Status);
        Assert.Equal(100m, order.LimitPrice);
    }

    [Fact]
    public async Task PlayerOrderOutsideTheAllowedRangeIsCancelledEvenThoughItIsNeverAgeRepriced()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;

        var placed = await market.PlaceOrderAsync(player.Id, company.Id, OrderType.Buy, 2, 110m);
        await SetBandAsync(company.Id, reference: 200m, lower: 170m, upper: 220m);

        await StepAsync(market, 1);

        var order = await context.Orders.FindAsync(placed.Order!.Id);
        Assert.Equal(OrderStatus.Cancelled, order!.Status);
    }

    private async Task SetBandAsync(int companyId, decimal reference, decimal lower, decimal upper)
    {
        context.PriceBandStates.Add(new PriceBandState
        {
            CompanyId = companyId,
            State = LuldState.Normal,
            ReferencePrice = reference,
            LowerBandPrice = lower,
            UpperBandPrice = upper,
        });
        await context.SaveChangesAsync();
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
            context.Holdings.Add(new Holding
            {
                ParticipantId = poor.Id,
                CompanyId = company.Id,
                Quantity = sharesEach,
                AverageCost = price,
            });

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

    private sealed class FixedRoll(double value) : Random
    {
        public override double NextDouble() => value;
    }
}
