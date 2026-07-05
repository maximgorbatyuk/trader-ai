using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the crisis roll with a scripted Random so the probability gate and the per-industry impact are
// forced deterministically. The gate consumes one draw per scope evaluated (global first, then local unless
// global already fired), so the queued doubles below mirror that order exactly.
public sealed class CrisisServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public CrisisServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private CrisisService Service(bool enabled, Random random) =>
        new(context, Options.Create(new CrisisOptions { Enabled = enabled }), new MarketImpactService(context), random);

    [Fact]
    public async Task DisabledDoesNotTrigger()
    {
        await SeedAsync(industryCount: 1, cycleNumber: 500);
        var (market, cycle) = await MarketAndCycleAsync();

        var result = await Service(enabled: false, new ScriptedRandom([], []))
            .MaybeTriggerForCycleAsync(market, cycle, DateTime.UtcNow);

        Assert.False(result.Triggered);
        Assert.Equal(0, await context.Crises.CountAsync());
    }

    [Fact]
    public async Task NoCrisisDuringTheQuietPeriod()
    {
        await SeedAsync(industryCount: 1, cycleNumber: 50);
        var (market, cycle) = await MarketAndCycleAsync();

        // Both scopes are still in their quiet window at cycle 50, so the chance is zero for each; the two
        // queued draws are consumed by the global then local gate and never clear the bar.
        var result = await Service(enabled: true, new ScriptedRandom([0d, 0d], []))
            .MaybeTriggerForCycleAsync(market, cycle, DateTime.UtcNow);

        Assert.False(result.Triggered);
        Assert.Equal(0, await context.Crises.CountAsync());
    }

    [Fact]
    public async Task LocalCrisisDropsItsSectorCancelsBuyOrdersAndResetsTheClock()
    {
        await SeedAsync(industryCount: 1, cycleNumber: 150);
        var (market, cycle) = await MarketAndCycleAsync();
        var company = await context.Companies.FirstAsync();
        var buyer = await AddBuyerWithOpenBuyOrderAsync(company.Id, cycle.Id);

        // doubles: global gate (chance 0 at cycle 150 → no fire), local gate (chance 1.0 → fire), one impact
        // draw → 5%. ints: industry count, one pick, three content picks.
        var random = new ScriptedRandom([0.5d, 0.0d, 0.0d], [1, 0, 0, 0, 0]);
        var result = await Service(enabled: true, random).MaybeTriggerForCycleAsync(market, cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.True(result.Triggered);
        var crisis = Assert.Single(result.Crises);
        Assert.Equal(CrisisScope.Local, crisis.Scope);
        var link = Assert.Single(crisis.Industries);
        Assert.Equal(5m, link.ImpactPercent);

        var latest = await context.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync();
        Assert.Equal(95m, latest.Price);

        var order = await context.Orders.AsNoTracking().FirstAsync(order => order.ParticipantId == buyer.Id);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(0m, order.ReservedCashAmount);
        var refreshedBuyer = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == buyer.Id);
        Assert.Equal(0m, refreshedBuyer.ReservedBalance);

        var savedMarket = await context.Markets.AsNoTracking().FirstAsync();
        Assert.Equal(150, savedMarket.LastLocalCrisisCycleNumber);
        Assert.Equal(0, savedMarket.LastGlobalCrisisCycleNumber);
    }

    [Fact]
    public async Task GlobalCrisisHitsAShareOfAllIndustries()
    {
        await SeedAsync(industryCount: 10, cycleNumber: 350);
        var (market, cycle) = await MarketAndCycleAsync();

        // doubles: global gate (chance 1.0 → fire), affected-share draw (0 → the 30% floor → 3 of 10), then
        // three impact draws. ints: three picks, three content picks. The local gate is never drawn because
        // the global crisis already fired this cycle.
        var random = new ScriptedRandom([0.0d, 0.0d, 0.0d, 0.0d, 0.0d], [0, 0, 0, 0, 0, 0]);
        var result = await Service(enabled: true, random).MaybeTriggerForCycleAsync(market, cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.True(result.Triggered);
        var crisis = Assert.Single(result.Crises);
        Assert.Equal(CrisisScope.Global, crisis.Scope);
        Assert.Equal(3, crisis.Industries.Count);
        Assert.All(crisis.Industries, link => Assert.Equal(5m, link.ImpactPercent));

        var savedMarket = await context.Markets.AsNoTracking().FirstAsync();
        Assert.Equal(350, savedMarket.LastGlobalCrisisCycleNumber);
    }

    [Fact]
    public async Task GlobalCrisisStampsItsWindowAndOpensTheTimeline()
    {
        await SeedAsync(industryCount: 10, cycleNumber: 350);
        var (market, cycle) = await MarketAndCycleAsync();

        // Same forced draws as the share-of-industries case: global fires, the 30% floor picks 3 of 10.
        var random = new ScriptedRandom([0.0d, 0.0d, 0.0d, 0.0d, 0.0d], [0, 0, 0, 0, 0, 0]);
        var result = await Service(enabled: true, random).MaybeTriggerForCycleAsync(market, cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var crisis = Assert.Single(result.Crises);
        Assert.Equal(350, crisis.TriggeredInCycleNumber);
        Assert.Equal(20, crisis.DurationCycles);
        Assert.Equal(3, await context.CrisisEvents.CountAsync(row => row.Type == CrisisEventType.IndustryShock));
    }

    [Fact]
    public async Task GetActiveCrisisTracksItsDurationWindow()
    {
        await SeedAsync(industryCount: 1, cycleNumber: 200);
        context.Crises.Add(new Crisis
        {
            Title = "Shock",
            Content = "Body",
            Scope = CrisisScope.Global,
            TriggeredInCycleId = 1,
            TriggeredInCycleNumber = 100,
            DurationCycles = 20,
            TriggeredAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var service = Service(enabled: true, new ScriptedRandom([], []));

        Assert.Null(await service.GetActiveCrisisAsync(100));    // the trigger cycle is not yet inside the window
        Assert.NotNull(await service.GetActiveCrisisAsync(101));  // first active cycle
        Assert.NotNull(await service.GetActiveCrisisAsync(120));  // last active cycle
        Assert.Null(await service.GetActiveCrisisAsync(121));    // window has closed
    }

    private async Task<(Market Market, MarketCycle Cycle)> MarketAndCycleAsync()
    {
        var market = await context.Markets.FirstAsync();
        var cycle = await context.MarketCycles.FirstAsync(cycle => cycle.Id == market.CurrentCycleId);
        return (market, cycle);
    }

    private async Task<Participant> AddBuyerWithOpenBuyOrderAsync(int companyId, int cycleId)
    {
        var now = DateTime.UtcNow;
        var buyer = new Participant
        {
            Name = "Buyer",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Aggressive,
            RiskProfile = RiskProfile.High,
            InitialBalance = 5000m,
            CurrentBalance = 5000m,
            ReservedBalance = 200m,
            IsActive = true,
        };
        context.Participants.Add(buyer);
        await context.SaveChangesAsync();

        context.Orders.Add(new Order
        {
            ParticipantId = buyer.Id,
            CompanyId = companyId,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 2,
            FilledQuantity = 0,
            LimitPrice = 100m,
            ReservedCashAmount = 200m,
            CreatedInCycleId = cycleId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync();
        return buyer;
    }

    private async Task SeedAsync(int industryCount, int cycleNumber)
    {
        var now = DateTime.UtcNow;

        var cycle = new MarketCycle { CycleNumber = cycleNumber, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);

        var market = new Market { Name = "Demo Market", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);
        await context.SaveChangesAsync();

        for (var index = 0; index < industryCount; index++)
        {
            var industry = new Industry { Name = $"Industry {index}" };
            context.Industries.Add(industry);
            await context.SaveChangesAsync();

            var company = new Company
            {
                Name = $"Company {index}",
                IndustryId = industry.Id,
                IssuedSharesCount = 100,
                CreatedAt = now,
                UpdatedAt = now,
            };
            context.Companies.Add(company);
            await context.SaveChangesAsync();

            context.PriceSnapshots.Add(new PriceSnapshot
            {
                CompanyId = company.Id,
                Price = 100m,
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

    // Returns queued draws so every random branch is forced; throws if drawn past the script.
    private sealed class ScriptedRandom(double[] doubles, int[] ints) : Random
    {
        private readonly Queue<double> doubles = new(doubles);
        private readonly Queue<int> ints = new(ints);

        public override double NextDouble() => doubles.Dequeue();

        public override int Next(int maxValue) => ints.Dequeue();

        public override int Next(int minValue, int maxValue) => ints.Dequeue();
    }
}
