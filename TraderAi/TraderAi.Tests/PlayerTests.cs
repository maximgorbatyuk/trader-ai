using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Exercises the human-player participant against the same in-memory SQLite fixture the other service tests
// use, driving real cycles with a no-op engine (so only hand-placed orders and the player move) and scripted
// rolls where a deterministic balance, dividend rate, or reprice decision is needed.
public sealed class PlayerTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public PlayerTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private MarketService Service(Random random, IDecisionEngine? decisionEngine = null) =>
        new(context, new MatchingEngine(context), decisionEngine ?? new NoOpDecisionEngine(), new MarketCycleLock(), random);

    // Behavior 1: creation yields an active Player with a well-formed, in-range balance and trims the name.
    [Fact]
    public async Task CreatePlayerYieldsActivePlayerWithWholeDollarBalanceInRange()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        var result = await Service(new Random(20260702)).CreatePlayerAsync("  Ada  ");

        Assert.True(result.Success);
        var player = result.Player!;
        Assert.Equal(ParticipantType.Player, player.Type);
        Assert.True(player.IsActive);
        Assert.Equal("Ada", player.Name);
        Assert.Equal(0m, player.ReservedBalance);
        Assert.Equal(player.InitialBalance, player.CurrentBalance);
        Assert.InRange(player.InitialBalance, 10000m, 200000m);
        Assert.Equal(decimal.Truncate(player.InitialBalance), player.InitialBalance);
    }

    // Behavior 1: only one player may exist, so a second creation is refused.
    [Fact]
    public async Task SecondCreatePlayerAttemptFails()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var service = Service(new Random(1));
        Assert.True((await service.CreatePlayerAsync("First")).Success);

        var second = await service.CreatePlayerAsync("Second");

        Assert.False(second.Success);
        Assert.Null(second.Player);
        Assert.Equal("A player already exists.", second.Error);
    }

    // Behavior 1: a player cannot join before a market is seeded.
    [Fact]
    public async Task CreatePlayerWithoutAMarketFails()
    {
        var result = await Service(new Random(1)).CreatePlayerAsync("Nobody");

        Assert.False(result.Success);
        Assert.Null(result.Player);
        Assert.Equal("No market exists.", result.Error);
    }

    // Behavior 1: a null/blank name falls back to the default label.
    [Fact]
    public async Task BlankNameFallsBackToDefaultPlayerName()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        var result = await Service(new Random(1)).CreatePlayerAsync("   ");

        Assert.True(result.Success);
        Assert.Equal("Player", result.Player!.Name);
    }

    // Behavior 2: ageing never touches the player's order, while an identical Individual order is force-cancelled
    // at the age cap with its reserved cash released.
    [Fact]
    public async Task PlayerBuyOrderSurvivesAgeingWhileIdenticalIndividualOrderIsCancelled()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var bob = await context.Participants.FirstAsync(participant => participant.Name == "Bob");
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));

        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        var playerOrder = (await market.PlaceOrderAsync(player.Id, company.Id, OrderType.Buy, 2, 110m)).Order!;
        var bobOrder = (await market.PlaceOrderAsync(bob.Id, company.Id, OrderType.Buy, 2, 110m)).Order!;

        await StepAsync(market, 16);

        var survivingPlayerOrder = await context.Orders.FindAsync(playerOrder.Id);
        Assert.Equal(OrderStatus.Open, survivingPlayerOrder!.Status);
        Assert.Equal(110m, survivingPlayerOrder.LimitPrice);
        Assert.Equal(220m, survivingPlayerOrder.ReservedCashAmount);

        var cancelledBobOrder = await context.Orders.FindAsync(bobOrder.Id);
        Assert.Equal(OrderStatus.Cancelled, cancelledBobOrder!.Status);
        Assert.Equal(0m, cancelledBobOrder.ReservedCashAmount);
        await context.Entry(bob).ReloadAsync();
        Assert.Equal(0m, bob.ReservedBalance);
        Assert.True(await context.MoneyTransactions.AnyAsync(money =>
            money.RelatedOrderId == cancelledBobOrder.Id && money.Type == MoneyTransactionType.Release));
    }

    // Behavior 3: a price-drop shock cancels an Individual's stale bid but leaves the player's bid open.
    [Fact]
    public async Task PriceDropCancelsIndividualBidButLeavesPlayerBidOpen()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var bob = await context.Participants.FirstAsync(participant => participant.Name == "Bob");
        var company = await context.Companies.FirstAsync();
        var marketRow = await context.Markets.FirstAsync();
        var cycleId = marketRow.CurrentCycleId!.Value;
        var market = Service(new FixedRoll(0d));

        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        var playerOrder = (await market.PlaceOrderAsync(player.Id, company.Id, OrderType.Buy, 2, 110m)).Order!;
        var bobOrder = (await market.PlaceOrderAsync(bob.Id, company.Id, OrderType.Buy, 2, 110m)).Order!;

        // The impact service only stages changes; the caller owns the save, matching the cycle-advance path.
        await new MarketImpactService(context)
            .ApplyImpactAsync(NewsImpactDirection.Decrease, new[] { company.Id }, 10m, cycleId, DateTime.UtcNow);
        await context.SaveChangesAsync();

        await context.Entry(playerOrder).ReloadAsync();
        await context.Entry(bobOrder).ReloadAsync();
        Assert.Equal(OrderStatus.Open, playerOrder.Status);
        Assert.Equal(OrderStatus.Cancelled, bobOrder.Status);
    }

    // Behavior 4: a share-holding player is credited a dividend when the schedule comes due.
    [Fact]
    public async Task PlayerHoldingSharesReceivesADividendWhenTheScheduleFires()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        await GiveSharesAsync(company.Id, player.Id, 10, 100m);

        var marketRow = await context.Markets.FirstAsync();
        var dueCycleId = marketRow.CurrentCycleId!.Value;
        marketRow.NextDividendCycleNumber = 1;
        company.CashBalance = 0.20m;
        await context.SaveChangesAsync();

        await market.StepCycleAsync();

        // 10 shares at price 100 with the floor rate (0.01%) pay 0.01 each, so 0.10 in total.
        var dividend = await context.MoneyTransactions.SingleAsync(money =>
            money.ParticipantId == player.Id && money.Type == MoneyTransactionType.Dividend);
        Assert.Equal(0.1m, dividend.Amount);
        Assert.Equal(dueCycleId, dividend.CreatedInCycleId);
    }

    // Behavior 5: a worth snapshot is written for every trader each cycle — the two seeded Individuals plus the
    // player — and each is tagged with the completed cycle.
    [Fact]
    public async Task AdvancingWritesAWorthSnapshotForEveryTraderTaggedWithTheCompletedCycle()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        var completedCycleId = (await context.Markets.FirstAsync()).CurrentCycleId!.Value;

        await market.StepCycleAsync();

        var snapshots = await context.ParticipantWorthSnapshots.ToListAsync();
        Assert.Equal(3, snapshots.Count);
        Assert.All(snapshots, snapshot => Assert.Equal(completedCycleId, snapshot.CreatedInCycleId));
        Assert.Contains(snapshots, snapshot => snapshot.ParticipantId == player.Id);
    }

    // Behavior 5: snapshots cover every trader, so an advance without a human player still records the two
    // seeded Individuals.
    [Fact]
    public async Task AdvancingWithoutAPlayerStillWritesSnapshotsForOtherTraders()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        await Service(new FixedRoll(0d)).StepCycleAsync();

        Assert.Equal(2, await context.ParticipantWorthSnapshots.CountAsync());
    }

    // Behavior 5: the snapshot captures cash and holdings valued at the completing cycle's latest price.
    [Fact]
    public async Task SnapshotCapturesBalanceAndHoldingsValueAtCompletion()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        await GiveSharesAsync(company.Id, player.Id, 10, 100m);

        await market.StepCycleAsync();

        await context.Entry(player).ReloadAsync();
        var snapshot = await context.ParticipantWorthSnapshots.SingleAsync(snapshot => snapshot.ParticipantId == player.Id);
        Assert.Equal(player.CurrentBalance, snapshot.Balance);
        Assert.Equal(1000m, snapshot.HoldingsValue);
    }

    // Behavior 5: two advances produce last-cycle deltas that match the contract math. A dividend on the second
    // cycle is the only balance mover, so both the money and worth deltas equal that payout.
    [Fact]
    public async Task TwoAdvancesProduceLastCycleDeltasConsistentWithTheContract()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        await GiveSharesAsync(company.Id, player.Id, 10, 100m);

        var marketRow = await context.Markets.FirstAsync();
        marketRow.NextDividendCycleNumber = 2;
        company.CashBalance = 0.20m;
        await context.SaveChangesAsync();
        var startingBalance = player.CurrentBalance;

        await market.StepCycleAsync();
        await market.StepCycleAsync();

        var snapshots = await context.ParticipantWorthSnapshots
            .Where(snapshot => snapshot.ParticipantId == player.Id)
            .OrderBy(snapshot => snapshot.Id)
            .ToListAsync();
        Assert.Equal(2, snapshots.Count);

        var previous = snapshots[0];
        var latest = snapshots[1];
        Assert.Equal(startingBalance, previous.Balance);
        Assert.Equal(startingBalance + 0.1m, latest.Balance);

        var moneyChange = latest.Balance - previous.Balance;
        var worthChange = (latest.Balance + latest.HoldingsValue) - (previous.Balance + previous.HoldingsValue);
        Assert.Equal(0.1m, moneyChange);
        Assert.Equal(0.1m, worthChange);
    }

    // Behavior 6: cancelling a player buy releases the reserved cash and records a Release transaction.
    [Fact]
    public async Task CancellingPlayerBuyReleasesReservedCashAndWritesRelease()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        var order = (await market.PlaceOrderAsync(player.Id, company.Id, OrderType.Buy, 2, 110m)).Order!;

        var result = await market.CancelPlayerOrderAsync(order.Id);

        Assert.True(result.Success);
        Assert.Equal(OrderStatus.Cancelled, result.Order!.Status);
        Assert.Equal(0m, result.Order.ReservedCashAmount);
        await context.Entry(player).ReloadAsync();
        Assert.Equal(0m, player.ReservedBalance);
        var release = await context.MoneyTransactions.SingleAsync(money =>
            money.RelatedOrderId == order.Id && money.Type == MoneyTransactionType.Release);
        Assert.Equal(player.Id, release.ParticipantId);
        Assert.Equal(220m, release.Amount);
    }

    // Behavior 6: cancelling a player sell frees the shares it had listed.
    [Fact]
    public async Task CancellingPlayerSellFreesItsListing()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        await GiveSharesAsync(company.Id, player.Id, 5, 100m);
        var order = (await market.PlaceOrderAsync(player.Id, company.Id, OrderType.Sell, 5, 110m)).Order!;
        Assert.Equal(1, await context.Orders.CountAsync(sell =>
            sell.ParticipantId == player.Id && sell.Type == OrderType.Sell && sell.Status == OrderStatus.Open));

        var result = await market.CancelPlayerOrderAsync(order.Id);

        Assert.True(result.Success);
        Assert.Equal(OrderStatus.Cancelled, result.Order!.Status);
        // Cancelling frees the listing: no open sell remains and the player keeps all five shares.
        Assert.Equal(0, await context.Orders.CountAsync(sell =>
            sell.ParticipantId == player.Id && sell.Type == OrderType.Sell
            && (sell.Status == OrderStatus.Open || sell.Status == OrderStatus.PartiallyFilled)));
        Assert.Equal(5, await context.Holdings.Where(holding => holding.ParticipantId == player.Id).SumAsync(holding => holding.Quantity));
    }

    // Behavior 6: the player cannot cancel an order that is not its own.
    [Fact]
    public async Task CancellingAnotherParticipantsOrderFails()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var bob = await context.Participants.FirstAsync(participant => participant.Name == "Bob");
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));
        await market.CreatePlayerAsync("Ada");
        var bobOrder = (await market.PlaceOrderAsync(bob.Id, company.Id, OrderType.Buy, 2, 110m)).Order!;

        var result = await market.CancelPlayerOrderAsync(bobOrder.Id);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
        await context.Entry(bobOrder).ReloadAsync();
        Assert.Equal(OrderStatus.Open, bobOrder.Status);
    }

    // Behavior 6: an order that is no longer open cannot be cancelled.
    [Fact]
    public async Task CancellingAFilledOrderFails()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        var order = (await market.PlaceOrderAsync(player.Id, company.Id, OrderType.Buy, 2, 110m)).Order!;

        order.Status = OrderStatus.Filled;
        order.FilledQuantity = order.Quantity;
        await context.SaveChangesAsync();

        var result = await market.CancelPlayerOrderAsync(order.Id);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    // Behavior 6: with no player in the market, the cancel action fails outright.
    [Fact]
    public async Task CancellingWithNoPlayerFails()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var bob = await context.Participants.FirstAsync(participant => participant.Name == "Bob");
        var company = await context.Companies.FirstAsync();
        var market = Service(new FixedRoll(0d));
        var bobOrder = (await market.PlaceOrderAsync(bob.Id, company.Id, OrderType.Buy, 2, 110m)).Order!;

        var result = await market.CancelPlayerOrderAsync(bobOrder.Id);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    // Behavior 7: the automated decision pass never trades on the player's behalf, even when the deterministic
    // engine would otherwise buy for a cash-rich, share-less trader.
    [Fact]
    public async Task GenerateDecisionsPlacesNoOrdersForThePlayer()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = Service(new Random(1), new DeterministicDecisionEngine());
        var player = (await market.CreatePlayerAsync("Ada")).Player!;

        var result = await market.GenerateDecisionsAsync();

        Assert.True(result.Success);
        Assert.True(result.OrdersPlaced > 0);
        Assert.Equal(0, await context.Orders.CountAsync(order => order.ParticipantId == player.Id));
    }

    // Allowed-range entry: the wider participant range boundaries are inclusive for both sides at a $100 reference.
    [Fact]
    public async Task AllowedRangeBoundaryPricesAreAcceptedForBothSides()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var market = Service(new Random(1));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        await GiveSharesAsync(company.Id, player.Id, 5, 100m);

        var lowBuy = await market.PlaceOrderAsync(player.Id, company.Id, OrderType.Buy, 1, 75m);
        Assert.True(lowBuy.Success, lowBuy.Error);
        await market.CancelPlayerOrderAsync(lowBuy.Order!.Id);

        var highSell = await market.PlaceOrderAsync(player.Id, company.Id, OrderType.Sell, 1, 115m);
        Assert.True(highSell.Success, highSell.Error);
    }

    // Allowed-range entry: a price one cent beyond either boundary is rejected with the actionable message.
    [Fact]
    public async Task PricesOneCentBeyondTheAllowedRangeAreRejectedWithBothRanges()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var market = Service(new Random(1));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;
        await GiveSharesAsync(company.Id, player.Id, 5, 100m);

        var lowBuy = await market.PlaceOrderAsync(player.Id, company.Id, OrderType.Buy, 1, 74.99m);
        var highSell = await market.PlaceOrderAsync(player.Id, company.Id, OrderType.Sell, 1, 115.01m);

        Assert.False(lowBuy.Success);
        Assert.Equal(
            "Limit price must be between $75.00 and $115.00. The current executable band is $85.00–$110.00.",
            lowBuy.Error);
        Assert.False(highSell.Success);
    }

    // Allowed-range entry: a price inside the allowed range but outside the executable band is accepted and rests open.
    [Fact]
    public async Task InsideAllowedRangeButOutsideActiveBandIsAcceptedAndRestsOpen()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var market = Service(new Random(1));
        var player = (await market.CreatePlayerAsync("Ada")).Player!;

        var waiting = await market.PlaceOrderAsync(player.Id, company.Id, OrderType.Buy, 1, 80m);

        Assert.True(waiting.Success, waiting.Error);
        Assert.Equal(OrderStatus.Open, waiting.Order!.Status);
        Assert.Equal(80m, waiting.Order.LimitPrice);
    }

    private static async Task StepAsync(MarketService market, int times)
    {
        for (var step = 0; step < times; step++)
        {
            await market.StepCycleAsync();
        }
    }

    private async Task GiveSharesAsync(int companyId, int ownerId, int count, decimal price)
    {
        context.Holdings.Add(new Holding
        {
            ParticipantId = ownerId,
            CompanyId = companyId,
            Quantity = count,
            AverageCost = price,
        });

        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    // Returns a fixed reprice/rate roll and the low end of every ranged draw, so order ageing, the dividend
    // rate, the dividend interval, and the player's starting balance are all deterministic.
    private sealed class FixedRoll(double value) : Random
    {
        public override double NextDouble() => value;

        public override int Next(int minValue, int maxValue) => minValue;
    }
}
