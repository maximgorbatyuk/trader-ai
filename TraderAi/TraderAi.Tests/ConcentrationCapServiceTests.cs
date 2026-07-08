using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// The concentration cap is deterministic (no random draws): any company worth more than the weight cap of
// total market capitalisation has its price cut, and companies under the cap are untouched.
public sealed class ConcentrationCapServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private MarketCycle cycle = null!;
    private int industryId;
    private int companySequence;

    public ConcentrationCapServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private ConcentrationCapService Service(bool enabled = true, decimal maxWeight = 20m, decimal cut = 25m) =>
        new(
            context,
            Options.Create(new ConcentrationCapOptions
            {
                Enabled = enabled,
                MaxSingleCompanyWeightPercent = maxWeight,
                PriceCutPercent = cut,
            }),
            new MarketImpactService(context));

    [Fact]
    public async Task DisabledDoesNothing()
    {
        await SeedAsync();
        var big = await AddCompanyAsync(shares: 1000, price: 100m); // 100k of ~120k total = 83%.
        await AddCompanyAsync(shares: 1000, price: 10m);
        await AddCompanyAsync(shares: 1000, price: 10m);

        await Service(enabled: false).ProcessForCycleAsync(cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(100m, await LatestPriceAsync(big));
        Assert.Equal(0, await context.NewsPosts.CountAsync());
    }

    [Fact]
    public async Task OverCapCompanyIsCutAndUnderCapCompanyIsUntouched()
    {
        await SeedAsync();
        var big = await AddCompanyAsync(shares: 1000, price: 100m); // 100k of 120k = 83% > 20%.
        var smallA = await AddCompanyAsync(shares: 1000, price: 10m); // 10k = 8.3%.
        var smallB = await AddCompanyAsync(shares: 1000, price: 10m);

        await Service().ProcessForCycleAsync(cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(75m, await LatestPriceAsync(big)); // 100 cut 25%.
        Assert.Equal(10m, await LatestPriceAsync(smallA));
        Assert.Equal(10m, await LatestPriceAsync(smallB));

        var news = await context.NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal(NewsImpactScope.Company, news.Scope);
        Assert.Equal(NewsImpactDirection.Decrease, news.Direction);
        Assert.Equal(big, news.TargetCompanyId);
    }

    [Fact]
    public async Task EveryOverCapCompanyIsCut()
    {
        await SeedAsync();
        var a = await AddCompanyAsync(shares: 100, price: 400m); // 40k of 100k = 40%.
        var b = await AddCompanyAsync(shares: 100, price: 300m); // 30k = 30%.
        var c = await AddCompanyAsync(shares: 100, price: 150m); // 15k = 15%.
        var d = await AddCompanyAsync(shares: 100, price: 150m); // 15k = 15%.

        await Service().ProcessForCycleAsync(cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(300m, await LatestPriceAsync(a)); // both over-cap names cut 25%.
        Assert.Equal(225m, await LatestPriceAsync(b));
        Assert.Equal(150m, await LatestPriceAsync(c));
        Assert.Equal(150m, await LatestPriceAsync(d));
        Assert.Equal(2, await context.NewsPosts.CountAsync());
    }

    [Fact]
    public async Task NoCompanyOverTheCapDoesNothing()
    {
        await SeedAsync();
        for (var index = 0; index < 6; index++)
        {
            await AddCompanyAsync(shares: 1000, price: 10m); // six equal names, ~16.7% each, all under 20%.
        }

        await Service().ProcessForCycleAsync(cycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.NewsPosts.CountAsync());
        Assert.Equal(6, await context.PriceSnapshots.CountAsync()); // no new cut snapshots.
    }

    private async Task SeedAsync()
    {
        var now = DateTime.UtcNow;
        cycle = new MarketCycle { CycleNumber = 1, Status = CycleStatus.Running, StartedAt = now };
        context.MarketCycles.Add(cycle);
        var industry = new Industry { Name = "Tech" };
        context.Industries.Add(industry);
        var market = new Market { Name = "Demo", Status = MarketStatus.Running, CreatedAt = now, UpdatedAt = now };
        context.Markets.Add(market);
        await context.SaveChangesAsync();
        industryId = industry.Id;
        market.CurrentCycleId = cycle.Id;
        await context.SaveChangesAsync();
    }

    private async Task<int> AddCompanyAsync(int shares, decimal price)
    {
        var now = DateTime.UtcNow;
        var company = new Company
        {
            Name = $"Co{++companySequence}",
            IndustryId = industryId,
            IssuedSharesCount = shares,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();

        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = price,
            Capitalization = price * shares,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
        });
        await context.SaveChangesAsync();
        return company.Id;
    }

    private async Task<decimal> LatestPriceAsync(int companyId) =>
        (await context.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == companyId)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync())
        .Price;

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
