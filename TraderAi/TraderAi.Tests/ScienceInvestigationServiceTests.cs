using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the science-investigation roll with a scripted Random so the probability gate and the per-industry
// impact are forced deterministically. The gate consumes one draw per cycle evaluated, so the queued doubles
// below mirror that order exactly.
public sealed class ScienceInvestigationServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public ScienceInvestigationServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private ScienceInvestigationService Service(bool enabled, Random random) =>
        new(context, Options.Create(new ScienceInvestigationOptions { Enabled = enabled }), new MarketImpactService(context), random);

    [Fact]
    public async Task DisabledDoesNotTrigger()
    {
        await SeedAsync(industryCount: 1, cycleNumber: 500);
        var (market, cycle) = await MarketAndCycleAsync();

        var result = await Service(enabled: false, new ScriptedRandom([], []))
            .MaybeTriggerForCycleAsync(market, cycle, DateTime.UtcNow);

        Assert.False(result.Triggered);
        Assert.Equal(0, await context.ScienceInvestigations.CountAsync());
    }

    [Fact]
    public async Task NoInvestigationDuringQuietPeriod()
    {
        await SeedAsync(industryCount: 1, cycleNumber: 20);
        var (market, cycle) = await MarketAndCycleAsync();

        // At cycle 20 the chance is still zero (inside the 50-cycle quiet window); the single queued draw is
        // consumed by the gate and never clears the bar.
        var result = await Service(enabled: true, new ScriptedRandom([0d], []))
            .MaybeTriggerForCycleAsync(market, cycle, DateTime.UtcNow);

        Assert.False(result.Triggered);
        Assert.Equal(0, await context.ScienceInvestigations.CountAsync());
    }

    [Fact]
    public async Task LocalInvestigationRaisesItsSectorLeavesSellOrdersAndResetsTheClock()
    {
        await SeedAsync(industryCount: 1, cycleNumber: 100);
        var (market, cycle) = await MarketAndCycleAsync();
        var company = await context.Companies.FirstAsync();
        var seller = await AddSellerWithOpenSellOrderAsync(company.Id, cycle.Id);

        // doubles: gate (chance 1.0 at cycle 100 → fire), one impact draw → 0.5%. ints: industry count, one
        // pick, three content picks.
        var random = new ScriptedRandom([0.0d, 0.0d], [1, 0, 0, 0, 0]);
        var result = await Service(enabled: true, random).MaybeTriggerForCycleAsync(market, cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.True(result.Triggered);
        var investigation = result.Investigation!;
        var link = Assert.Single(investigation.Industries);
        Assert.Equal(0.5m, link.ImpactPercent);

        var latest = await context.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync();
        Assert.Equal(100.5m, latest.Price);

        // A science lift never touches the order book: the standing sell order stays open with its shares.
        var order = await context.Orders.AsNoTracking().FirstAsync(order => order.ParticipantId == seller.Id);
        Assert.Equal(OrderStatus.Open, order.Status);
        // The seller keeps its two shares — the science lift never touches holdings.
        Assert.Equal(2, await context.Holdings.Where(holding => holding.ParticipantId == seller.Id).SumAsync(holding => holding.Quantity));

        var savedMarket = await context.Markets.AsNoTracking().FirstAsync();
        Assert.Equal(100, savedMarket.LastScienceInvestigationCycleNumber);
    }

    [Fact]
    public async Task ActiveCrisisHalvesTheInvestigationChance()
    {
        await SeedAsync(industryCount: 1, cycleNumber: 100);
        var (market, cycle) = await MarketAndCycleAsync();

        // At cycle 100 the calm chance is 1.0, so a 0.6 draw would fire; a crisis halves it to 0.5 and the
        // same draw no longer clears the bar.
        var result = await Service(enabled: true, new ScriptedRandom([0.6d], []))
            .MaybeTriggerForCycleAsync(market, cycle, DateTime.UtcNow, duringCrisis: true);

        Assert.False(result.Triggered);
        Assert.Equal(0, await context.ScienceInvestigations.CountAsync());
    }

    private async Task<(Market Market, MarketCycle Cycle)> MarketAndCycleAsync()
    {
        var market = await context.Markets.FirstAsync();
        var cycle = await context.MarketCycles.FirstAsync(cycle => cycle.Id == market.CurrentCycleId);
        return (market, cycle);
    }

    private async Task<Participant> AddSellerWithOpenSellOrderAsync(int companyId, int cycleId)
    {
        var now = DateTime.UtcNow;
        var seller = new Participant
        {
            Name = "Seller",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 5000m,
            CurrentBalance = 5000m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(seller);
        await context.SaveChangesAsync();

        var order = new Order
        {
            ParticipantId = seller.Id,
            CompanyId = companyId,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = 2,
            FilledQuantity = 0,
            LimitPrice = 100m,
            ReservedCashAmount = 0m,
            CreatedInCycleId = cycleId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        context.Holdings.Add(new Holding
        {
            ParticipantId = seller.Id,
            CompanyId = companyId,
            Quantity = 2,
            AverageCost = 100m,
        });

        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return seller;
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
