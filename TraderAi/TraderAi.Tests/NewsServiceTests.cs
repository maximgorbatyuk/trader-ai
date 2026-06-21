using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives NewsService with a fully scripted Random so each random branch (publish, impact, scope, direction,
// magnitude) is forced, letting the impact math and persistence be asserted exactly.
public sealed class NewsServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public NewsServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private NewsService Service(NewsLoopOptions settings, Random random) =>
        new(context, new MarketCycleLock(), Options.Create(settings), random);

    [Fact]
    public async Task CompanyImpactSnapshotsTheTargetAtTheMovedPrice()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var cycleId = (await context.Markets.FirstAsync()).CurrentCycleId!.Value;

        var settings = new NewsLoopOptions { PublishProbability = 1.0, ImpactProbability = 1.0, CompanyScopeProbability = 1.0 };
        // doubles: publish, impact, impact-percent (floor → 0.1%), scope. ints: 4 content picks, direction (0 = Increase), company pick.
        var random = new ScriptedRandom([0d, 0d, 0d, 0d], [0, 0, 0, 0, 0, 0]);

        var result = await Service(settings, random).PublishRandomNewsAsync();

        Assert.True(result.Published);
        Assert.Equal(1, result.CompaniesMoved);

        var post = await context.NewsPosts.SingleAsync();
        Assert.Equal(NewsImpactScope.Company, post.Scope);
        Assert.Equal(NewsImpactDirection.Increase, post.Direction);
        Assert.Equal(0.10m, post.ImpactPercent);
        Assert.Equal(company.Id, post.TargetCompanyId);
        Assert.Equal(cycleId, post.PublishedInCycleId);

        // Seed price 100 moved up 0.1% lands at 100.10 and becomes the company's latest snapshot.
        var latest = await context.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync();
        Assert.Equal(100.10m, latest.Price);
    }

    [Fact]
    public async Task IndustryImpactMovesEveryCompanyInTheChosenIndustries()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var industry = await context.Industries.FirstAsync();

        var settings = new NewsLoopOptions { PublishProbability = 1.0, ImpactProbability = 1.0, CompanyScopeProbability = 0.0 };
        // scope double 0.0 is not < 0.0, so the industries branch is taken; ints add an industry-count and a distinct pick.
        var random = new ScriptedRandom([0d, 0d, 0d, 0d], [0, 0, 0, 0, 0, 1, 0]);

        var result = await Service(settings, random).PublishRandomNewsAsync();

        Assert.True(result.Published);
        Assert.Equal(1, result.CompaniesMoved);

        var post = await context.NewsPosts.Include(newsPost => newsPost.Industries).SingleAsync();
        Assert.Equal(NewsImpactScope.Industries, post.Scope);
        Assert.Null(post.TargetCompanyId);
        var link = Assert.Single(post.Industries);
        Assert.Equal(industry.Id, link.IndustryId);

        var snapshots = await context.PriceSnapshots.Where(snapshot => snapshot.CompanyId == company.Id).CountAsync();
        Assert.Equal(2, snapshots);
    }

    [Fact]
    public async Task PublishedPostWithoutImpactRecordsNoneScopeAndMovesNoPrice()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();

        var settings = new NewsLoopOptions { PublishProbability = 1.0, ImpactProbability = 0.0 };
        var random = new ScriptedRandom([0d, 0d], [0, 0, 0, 0]);

        var result = await Service(settings, random).PublishRandomNewsAsync();

        Assert.True(result.Published);
        Assert.Equal(0, result.CompaniesMoved);

        var post = await context.NewsPosts.SingleAsync();
        Assert.Equal(NewsImpactScope.None, post.Scope);
        Assert.Null(post.Direction);
        Assert.Null(post.ImpactPercent);

        // Only the seed snapshot remains for the company.
        Assert.Equal(1, await context.PriceSnapshots.CountAsync(snapshot => snapshot.CompanyId == company.Id));
    }

    [Fact]
    public async Task NoNewsIsPublishedWhenTheMarketIsNotRunning()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = await context.Markets.FirstAsync();
        market.Status = MarketStatus.Paused;
        await context.SaveChangesAsync();

        var settings = new NewsLoopOptions { PublishProbability = 1.0, ImpactProbability = 1.0 };
        var random = new ScriptedRandom([], []);

        var result = await Service(settings, random).PublishRandomNewsAsync();

        Assert.False(result.Published);
        Assert.Equal(0, await context.NewsPosts.CountAsync());
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    // Returns queued draws so every random branch in NewsService is forced; throws if drawn past the script.
    private sealed class ScriptedRandom(double[] doubles, int[] ints) : Random
    {
        private readonly Queue<double> doubles = new(doubles);
        private readonly Queue<int> ints = new(ints);

        public override double NextDouble() => doubles.Dequeue();

        public override int Next(int maxValue) => ints.Dequeue();

        public override int Next(int minValue, int maxValue) => ints.Dequeue();
    }
}
