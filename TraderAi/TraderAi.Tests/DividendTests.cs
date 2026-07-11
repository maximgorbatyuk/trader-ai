using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the real maintain-decide-advance tick with a no-op engine so only the dividend schedule moves,
// and a fixed roll so every company passes its pay roll and its rate lands at the floor (0.01%) for
// exact-amount assertions.
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

    private MarketService Service() => Service(new FloorRoll());

    private MarketService Service(Random roll) =>
        new(context, new MatchingEngine(context), new NoOpDecisionEngine(), new MarketCycleLock(), roll);

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

        // Alice holds 10 shares at price 100; the floor rate (0.01% of capitalisation, i.e. 0.01 per share) pays
        // 0.10 across her 10 shares.
        await context.Entry(alice).ReloadAsync();
        Assert.Equal(1000.1m, alice.CurrentBalance);

        var dividend = await context.MoneyTransactions.SingleAsync(money => money.Type == MoneyTransactionType.Dividend);
        Assert.Equal(alice.Id, dividend.ParticipantId);
        Assert.Equal(0.1m, dividend.Amount);
        Assert.Equal(dueCycleId, dividend.CreatedInCycleId);

        // Bob owns no shares, so he is never credited.
        await context.Entry(bob).ReloadAsync();
        Assert.Equal(5000m, bob.CurrentBalance);
    }

    [Fact]
    public async Task PayoutRecordsThePayingCompanyInTheBreakdown()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = await context.Markets.FirstAsync();
        market.NextDividendCycleNumber = 1;
        await context.SaveChangesAsync();

        var alice = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var holding = await context.Holdings.FirstAsync(row => row.ParticipantId == alice.Id);

        await Service().StepCycleAsync();

        var dividend = await context.MoneyTransactions.SingleAsync(money => money.Type == MoneyTransactionType.Dividend);
        var line = await context.DividendPayouts.SingleAsync(payout => payout.MoneyTransactionId == dividend.Id);
        Assert.Equal(holding.CompanyId, line.CompanyId);
        // A single paying company means its line carries the whole payout.
        Assert.Equal(dividend.Amount, line.Amount);
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

    [Fact]
    public async Task PayoutIsCappedAtThePerCompanyCeiling()
    {
        var now = DateTime.UtcNow;
        var cycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);
        var market = new Market { Name = "Demo", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);
        var industry = new Industry { Name = "Tech" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();

        var company = new Company { Name = "Acme", IndustryId = industry.Id, IssuedSharesCount = 2_000_000, CreatedAt = now, UpdatedAt = now };
        context.Companies.Add(company);
        var whale = new Participant
        {
            Name = "Whale",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 0m,
            CurrentBalance = 0m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(whale);
        await context.SaveChangesAsync();

        context.Holdings.Add(new Holding { ParticipantId = whale.Id, CompanyId = company.Id, Quantity = 2_000_000, AverageCost = 500m });
        context.PriceSnapshots.Add(new PriceSnapshot { CompanyId = company.Id, Price = 10_000m, CreatedInCycleId = cycle.Id, CreatedAt = now });
        market.CurrentCycleId = cycle.Id;
        market.NextDividendCycleNumber = 1;
        await context.SaveChangesAsync();

        await Service().StepCycleAsync();

        // Uncapped would be price*rate*qty = 10,000 * 0.0001 * 2,000,000 = 2,000,000; the ceiling holds it to 1,000,000.
        var dividend = await context.MoneyTransactions.SingleAsync(money => money.Type == MoneyTransactionType.Dividend);
        Assert.Equal(whale.Id, dividend.ParticipantId);
        Assert.Equal(1_000_000m, dividend.Amount);
    }

    [Fact]
    public async Task StableCapitalizationPaysOnAMidRollThatVolatileWouldReject()
    {
        // Baseline equals current capitalisation (100 x 1000), so the company is stable and rolls at 75%.
        var holder = await SeedSingleHolderAsync(baselineCapitalization: 100_000m);
        var company = await context.Companies.FirstAsync();

        // A 0.5 roll clears the 0.75 stable chance but would miss the 0.25 volatile one; then the rate floors.
        await Service(new QueuedRoll(0.5d, 0d)).StepCycleAsync();

        await context.Entry(holder).ReloadAsync();
        Assert.Equal(0.1m, holder.CurrentBalance);
        Assert.True(await context.MoneyTransactions.AnyAsync(money => money.Type == MoneyTransactionType.Dividend));

        await context.Entry(company).ReloadAsync();
        Assert.Equal(100_000m, company.LastDividendCapitalization);
    }

    [Fact]
    public async Task VolatileCapitalizationSkipsOnTheSameMidRollButStillRefreshesTheBaseline()
    {
        // Baseline is half of current capitalisation, a 100% move, so the company is volatile and rolls at 25%.
        var holder = await SeedSingleHolderAsync(baselineCapitalization: 50_000m);
        var company = await context.Companies.FirstAsync();

        // The same 0.5 roll misses the 0.25 volatile chance, so nothing pays — but the baseline still refreshes.
        await Service(new QueuedRoll(0.5d)).StepCycleAsync();

        await context.Entry(holder).ReloadAsync();
        Assert.Equal(0m, holder.CurrentBalance);
        Assert.False(await context.MoneyTransactions.AnyAsync(money => money.Type == MoneyTransactionType.Dividend));

        await context.Entry(company).ReloadAsync();
        Assert.Equal(100_000m, company.LastDividendCapitalization);
    }

    // A market due to pay this cycle with one company priced at 100 over 1000 issued shares (capitalisation
    // 100,000) and a single 10-share holder starting at zero cash.
    private async Task<Participant> SeedSingleHolderAsync(decimal? baselineCapitalization)
    {
        var now = DateTime.UtcNow;
        var cycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);
        var market = new Market { Name = "Demo", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);
        var industry = new Industry { Name = "Tech" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();

        var company = new Company
        {
            Name = "Acme",
            IndustryId = industry.Id,
            IssuedSharesCount = 1000,
            LastDividendCapitalization = baselineCapitalization,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Companies.Add(company);
        var holder = new Participant
        {
            Name = "Holder",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 0m,
            CurrentBalance = 0m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(holder);
        await context.SaveChangesAsync();

        context.Holdings.Add(new Holding { ParticipantId = holder.Id, CompanyId = company.Id, Quantity = 10, AverageCost = 100m });
        context.PriceSnapshots.Add(new PriceSnapshot { CompanyId = company.Id, Price = 100m, CreatedInCycleId = cycle.Id, CreatedAt = now });
        market.CurrentCycleId = cycle.Id;
        market.NextDividendCycleNumber = 1;
        await context.SaveChangesAsync();
        return holder;
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    // Returns the low end of every random draw: NextDouble 0 passes every pay roll and puts the dividend rate at
    // its floor, and the next-interval Next() lands on its minimum, all deterministic.
    private sealed class FloorRoll : Random
    {
        public override double NextDouble() => 0d;

        public override int Next(int minValue, int maxValue) => minValue;
    }

    // Hands back the queued NextDouble values in order (0 once drained) so the dividend pay roll and rate can be
    // scripted independently; Next() stays at its minimum, leaving the interval reschedule out of the sequence.
    private sealed class QueuedRoll(params double[] values) : Random
    {
        private readonly Queue<double> queue = new(values);

        public override double NextDouble() => queue.Count > 0 ? queue.Dequeue() : 0d;

        public override int Next(int minValue, int maxValue) => minValue;
    }
}
