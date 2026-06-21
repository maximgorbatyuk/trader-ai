using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the real maintain-decide-advance tick with a no-op engine so only the dividend schedule moves,
// and a fixed roll so the per-company rate lands at the floor (0.1%) for exact-amount assertions.
public sealed class DividendTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public DividendTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private MarketService Service() =>
        new(context, new MatchingEngine(context), new NoOpDecisionEngine(), new MarketCycleLock(), new FloorRoll());

    [Fact]
    public async Task DueCyclePaysEveryShareOwnerAndRecordsTheCredit()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = await context.Markets.FirstAsync();
        var dueCycleId = market.CurrentCycleId!.Value;
        market.NextDividendCycleNumber = 1;
        await context.SaveChangesAsync();

        var alice = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var bob = await context.Participants.FirstAsync(participant => participant.Name == "Bob");

        await Service().StepCycleAsync();

        // Alice holds 10 shares at price 100; 0.1% of price per share is 0.10, so 10 shares pay 1.00.
        await context.Entry(alice).ReloadAsync();
        Assert.Equal(1001m, alice.CurrentBalance);

        var dividend = await context.MoneyTransactions.SingleAsync(money => money.Type == MoneyTransactionType.Dividend);
        Assert.Equal(alice.Id, dividend.ParticipantId);
        Assert.Equal(1m, dividend.Amount);
        Assert.Equal(dueCycleId, dividend.CreatedInCycleId);

        // Bob owns no shares, so he is never credited.
        await context.Entry(bob).ReloadAsync();
        Assert.Equal(5000m, bob.CurrentBalance);
    }

    [Fact]
    public async Task NoDividendIsPaidBeforeTheScheduledCycle()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = await context.Markets.FirstAsync();
        market.NextDividendCycleNumber = 5;
        await context.SaveChangesAsync();

        var alice = await context.Participants.FirstAsync(participant => participant.Name == "Alice");

        await Service().StepCycleAsync();

        Assert.False(await context.MoneyTransactions.AnyAsync(money => money.Type == MoneyTransactionType.Dividend));
        await context.Entry(alice).ReloadAsync();
        Assert.Equal(1000m, alice.CurrentBalance);
    }

    [Fact]
    public async Task UnscheduledMarketArmsOnFirstAdvanceWithoutPaying()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = await context.Markets.FirstAsync();
        market.NextDividendCycleNumber = 0;
        await context.SaveChangesAsync();

        await Service().StepCycleAsync();

        Assert.False(await context.MoneyTransactions.AnyAsync(money => money.Type == MoneyTransactionType.Dividend));
        await context.Entry(market).ReloadAsync();
        Assert.True(market.NextDividendCycleNumber > 0);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    // Returns the low end of every random draw: NextDouble 0 puts the dividend rate at its floor, and the
    // next-interval Next() lands on its minimum, both deterministic.
    private sealed class FloorRoll : Random
    {
        public override double NextDouble() => 0d;

        public override int Next(int minValue, int maxValue) => minValue;
    }
}
