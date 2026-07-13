using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives free-share emission with a scripted Random. Companies below the capitalisation threshold or inside the
// cooldown draw nothing; a company that clears both draws one NextDouble to roll for an emission, one more for
// the size when it fires, then one Next per recipient it funds.
public sealed class ShareEmissionServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private int industryId;

    public ShareEmissionServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private ShareEmissionService Service(bool enabled, Random random) =>
        new(context, Options.Create(new ShareEmissionOptions { Enabled = enabled }), Options.Create(new RandomChanceRatesOptions()), random);

    [Fact]
    public async Task DisabledDoesNotEmit()
    {
        var cycle = await AddCycleAsync(60);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 500_000m, cycle);
        await AddTraderAsync();

        await Service(enabled: false, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.ShareEmissions.CountAsync());
    }

    [Fact]
    public async Task BelowCapitalizationThresholdDoesNotEmit()
    {
        var cycle = await AddCycleAsync(60);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        // Cap is only 100k, far below the $500M band, so no roll is even taken.
        await AddSnapshotAsync(company.Id, price: 100m, cycle);
        await AddTraderAsync();

        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.ShareEmissions.CountAsync());
        Assert.Equal(1000, (await context.Companies.AsNoTracking().FirstAsync()).IssuedSharesCount);
    }

    [Fact]
    public async Task RecentEmissionInsideCooldownBlocksAnother()
    {
        var earlier = await AddCycleAsync(30);
        var current = await AddCycleAsync(60);
        await SetupMarketAsync(current);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 500_000m, current);
        await AddTraderAsync();
        await AddEmissionAsync(company.Id, earlier);

        // Only 30 cycles on (< 50), so no draw is taken and no new emission is recorded.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.ShareEmissions.CountAsync());
    }

    [Fact]
    public async Task EmitsFreeSharesToANonHolderWithoutMovingPriceOrCancellingOrders()
    {
        var cycle = await AddCycleAsync(60);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 500_000m, cycle);
        var trader = await AddTraderAsync();

        // Emission fires (< 0.05), size 0.01 → 10 shares; one recipient shuffled into place.
        await Service(enabled: true, new ScriptedRandom([0.01d, 0.0d], [0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var emission = await context.ShareEmissions.AsNoTracking().SingleAsync();
        Assert.Equal(company.Id, emission.CompanyId);
        Assert.Equal(10, emission.SharesEmitted);
        Assert.Equal(1, emission.RecipientCount);

        var holding = await context.Holdings.AsNoTracking().SingleAsync(h => h.ParticipantId == trader.Id);
        Assert.Equal(10, holding.Quantity);
        Assert.Equal(10, holding.SettledQuantity);
        Assert.Equal(0m, holding.AverageCost);

        Assert.Equal(1010, (await context.Companies.AsNoTracking().FirstAsync()).IssuedSharesCount);

        var vehicle = await context.Orders.AsNoTracking()
            .SingleAsync(order => order.ParticipantId == null && order.Type == OrderType.Sell);
        Assert.Equal(OrderStatus.Filled, vehicle.Status);
        Assert.Equal(0m, vehicle.LimitPrice);
        Assert.Equal(10, vehicle.Quantity);

        var news = await context.NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal(NewsImpactScope.None, news.Scope);

        // No forced price snapshot and no cancellations.
        Assert.Equal(1, await context.PriceSnapshots.CountAsync());
        Assert.Equal(0, await context.Orders.CountAsync(order => order.Status == OrderStatus.Cancelled));
    }

    [Fact]
    public async Task GrantsAreCappedAtFiftySharesPerRecipient()
    {
        var cycle = await AddCycleAsync(60);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 500_000m, cycle);
        var first = await AddTraderAsync();
        var second = await AddTraderAsync();
        var third = await AddTraderAsync();

        // Size 0.10 → 100 shares; capped at 50 each, so two recipients are funded.
        await Service(enabled: true, new ScriptedRandom([0.01d, 1.0d], [0, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var emission = await context.ShareEmissions.AsNoTracking().SingleAsync();
        Assert.Equal(100, emission.SharesEmitted);
        Assert.Equal(2, emission.RecipientCount);

        var grants = await context.Holdings.AsNoTracking().Where(h => h.CompanyId == company.Id).ToListAsync();
        Assert.Equal(2, grants.Count);
        Assert.All(grants, grant => Assert.True(grant.Quantity <= 50));
        Assert.Equal(100, grants.Sum(grant => grant.Quantity));
        Assert.DoesNotContain(third.Id, grants.Select(grant => grant.ParticipantId));
        Assert.Equal(1100, (await context.Companies.AsNoTracking().FirstAsync()).IssuedSharesCount);
    }

    [Fact]
    public async Task EmissionSizeIsBoundedByAvailableRecipients()
    {
        var cycle = await AddCycleAsync(60);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 100_000);
        await AddSnapshotAsync(company.Id, price: 5_000m, cycle);
        await AddTraderAsync();
        await AddTraderAsync();

        // Nominal 10% of 100k = 10,000 shares, but two recipients cap it at 100 (50 each).
        await Service(enabled: true, new ScriptedRandom([0.01d, 1.0d], [0, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var emission = await context.ShareEmissions.AsNoTracking().SingleAsync();
        Assert.Equal(100, emission.SharesEmitted);
        Assert.Equal(100_100, (await context.Companies.AsNoTracking().FirstAsync()).IssuedSharesCount);
    }

    [Fact]
    public async Task EmissionChanceScalesWithCapitalization()
    {
        var cycle = await AddCycleAsync(60);
        await SetupMarketAsync(cycle);
        var small = await AddCompanyAsync(issuedShares: 1000);
        var large = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(small.Id, price: 500_000m, cycle);   // one band → 5%
        await AddSnapshotAsync(large.Id, price: 1_000_000m, cycle); // two bands → 10%
        await AddTraderAsync();
        await AddTraderAsync();
        await AddTraderAsync();

        // A 0.07 roll misses the small company's 5% but clears the large company's 10%.
        await Service(enabled: true, new ScriptedRandom([0.07d, 0.07d, 0.0d], [0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var emission = await context.ShareEmissions.AsNoTracking().SingleAsync();
        Assert.Equal(large.Id, emission.CompanyId);
        Assert.Equal(1000, (await context.Companies.AsNoTracking().FirstAsync(c => c.Id == small.Id)).IssuedSharesCount);
    }

    [Fact]
    public async Task ExistingHolderDoesNotReceiveFreeShares()
    {
        var cycle = await AddCycleAsync(60);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 500_000m, cycle);
        var holder = await AddTraderAsync();
        var newcomer = await AddTraderAsync();
        await AddHoldingAsync(holder.Id, company.Id, quantity: 5, averageCost: 100m);

        await Service(enabled: true, new ScriptedRandom([0.01d, 0.0d], [0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var newcomerHolding = await context.Holdings.AsNoTracking().SingleAsync(h => h.ParticipantId == newcomer.Id);
        Assert.Equal(10, newcomerHolding.Quantity);

        var holderHolding = await context.Holdings.AsNoTracking().SingleAsync(h => h.ParticipantId == holder.Id);
        Assert.Equal(5, holderHolding.Quantity);
    }

    [Fact]
    public async Task ParticipantWithASoldOutZeroQuantityHoldingIsExcluded()
    {
        var cycle = await AddCycleAsync(60);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 500_000m, cycle);
        var soldOut = await AddTraderAsync();
        var newcomer = await AddTraderAsync();
        // A sold-out position keeps a zero-quantity row; inserting a fresh holding here would hit the unique key.
        await AddHoldingAsync(soldOut.Id, company.Id, quantity: 0, averageCost: 100m);

        await Service(enabled: true, new ScriptedRandom([0.01d, 0.0d], [0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // The newcomer receives the emission; the sold-out holder still has just its single zero-quantity row.
        Assert.Equal(10, (await context.Holdings.AsNoTracking().SingleAsync(h => h.ParticipantId == newcomer.Id)).Quantity);
        var soldOutHoldings = await context.Holdings.AsNoTracking().Where(h => h.ParticipantId == soldOut.Id).ToListAsync();
        Assert.Equal(0, Assert.Single(soldOutHoldings).Quantity);
    }

    private async Task<MarketCycle> AddCycleAsync(int number)
    {
        var cycle = new MarketCycle { CycleNumber = number, Status = CycleStatus.Running, StartedAt = DateTime.UtcNow };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();
        return cycle;
    }

    private async Task SetupMarketAsync(MarketCycle currentCycle)
    {
        var now = DateTime.UtcNow;
        var industry = new Industry { Name = "Tech" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();
        industryId = industry.Id;

        context.Markets.Add(new Market
        {
            Name = "Demo Market",
            Status = MarketStatus.Running,
            CurrentCycleId = currentCycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync();
    }

    private async Task<Company> AddCompanyAsync(int issuedShares)
    {
        var now = DateTime.UtcNow;
        var company = new Company
        {
            Name = $"Acme {Guid.NewGuid():N}",
            IndustryId = industryId,
            IssuedSharesCount = issuedShares,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        return company;
    }

    private async Task AddSnapshotAsync(int companyId, decimal price, MarketCycle cycle)
    {
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = companyId,
            Price = price,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task<Participant> AddTraderAsync()
    {
        var trader = new Participant
        {
            Name = "Trader",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 10_000m,
            CurrentBalance = 10_000m,
            ReservedBalance = 0m,
            IsActive = true,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
    }

    private async Task AddHoldingAsync(int participantId, int companyId, int quantity, decimal averageCost)
    {
        context.Holdings.Add(new Holding
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Quantity = quantity,
            AverageCost = averageCost,
        });
        await context.SaveChangesAsync();
    }

    private async Task AddEmissionAsync(int companyId, MarketCycle cycle)
    {
        context.ShareEmissions.Add(new ShareEmission
        {
            CompanyId = companyId,
            SharesEmitted = 10,
            RecipientCount = 1,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
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
