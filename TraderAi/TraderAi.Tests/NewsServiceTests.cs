using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Exercises both news paths: manual posts (exact, caller-specified impact) and the automated path that only
// fires on its cycle schedule. A scripted Random forces the automated branches; manual impact is
// deterministic, so it uses a plain Random for the (irrelevant) wording.
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

    private NewsService Service(NewsOptions settings, Random random) =>
        new(context, new MarketCycleLock(), Options.Create(settings), new MarketImpactService(context), random);

    [Fact]
    public async Task ManualCompanyImpactSnapshotsTheTargetAtTheMovedPrice()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();

        var request = new ManualNewsRequest(NewsImpactScope.Company, "ufo", NewsImpactDirection.Increase, 5m, company.Id, null);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);

        Assert.True(result.Success);
        var post = result.Post!;
        Assert.Equal(NewsImpactScope.Company, post.Scope);
        Assert.Equal(NewsImpactDirection.Increase, post.Direction);
        Assert.Equal(5m, post.ImpactPercent);
        Assert.Equal(company.Id, post.TargetCompanyId);

        // Seed price 100 moved up 5% lands at 105 and becomes the company's latest snapshot.
        var latest = await context.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync();
        Assert.Equal(105m, latest.Price);
    }

    [Fact]
    public async Task ManualIndustriesImpactMovesEveryCompanyInTheChosenIndustries()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var industry = await context.Industries.FirstAsync();

        var request = new ManualNewsRequest(
            NewsImpactScope.Industries, "weather", NewsImpactDirection.Decrease, 5m, null, [industry.Id]);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);

        Assert.True(result.Success);
        var post = await context.NewsPosts.Include(newsPost => newsPost.Industries).SingleAsync();
        Assert.Equal(NewsImpactScope.Industries, post.Scope);
        Assert.Null(post.TargetCompanyId);
        var link = Assert.Single(post.Industries);
        Assert.Equal(industry.Id, link.IndustryId);

        var latest = await context.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync();
        Assert.Equal(95m, latest.Price);
    }

    [Fact]
    public async Task ManualDecreaseCancelsOpenBuyOrdersAndReleasesReservedCash()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var cycle = await context.MarketCycles.FirstAsync();
        var buyer = await context.Participants.FirstAsync(participant => participant.Name == "Bob");

        buyer.ReservedBalance = 200m;
        var order = new Order
        {
            ParticipantId = buyer.Id,
            CompanyId = company.Id,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 2,
            FilledQuantity = 0,
            LimitPrice = 100m,
            ReservedCashAmount = 200m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Orders.Add(order);
        await context.SaveChangesAsync();

        var request = new ManualNewsRequest(NewsImpactScope.Company, "ufo", NewsImpactDirection.Decrease, 10m, company.Id, null);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);

        Assert.True(result.Success);
        var saved = await context.Orders.AsNoTracking().FirstAsync(saved => saved.Id == order.Id);
        Assert.Equal(OrderStatus.Cancelled, saved.Status);
        Assert.Equal(0m, saved.ReservedCashAmount);
        var refreshedBuyer = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == buyer.Id);
        Assert.Equal(0m, refreshedBuyer.ReservedBalance);
        Assert.True(await context.MoneyTransactions
            .AnyAsync(transaction => transaction.RelatedOrderId == order.Id && transaction.Type == MoneyTransactionType.Release));
    }

    [Fact]
    public async Task ManualIncreaseCancelsOpenSellOrdersAndFreesTheirShares()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var cycle = await context.MarketCycles.FirstAsync();
        var seller = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var shareIds = await context.Shares
            .Where(share => share.OwnerId == seller.Id)
            .Select(share => share.Id)
            .Take(2)
            .ToListAsync();

        var order = new Order
        {
            ParticipantId = seller.Id,
            CompanyId = company.Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = 2,
            FilledQuantity = 0,
            LimitPrice = 100m,
            ReservedCashAmount = 0m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        foreach (var shareId in shareIds)
        {
            order.OrderShares.Add(new OrderShare { ShareId = shareId });
        }

        context.Orders.Add(order);
        await context.SaveChangesAsync();

        var request = new ManualNewsRequest(NewsImpactScope.Company, "ufo", NewsImpactDirection.Increase, 10m, company.Id, null);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);

        Assert.True(result.Success);
        var saved = await context.Orders.AsNoTracking().FirstAsync(saved => saved.Id == order.Id);
        Assert.Equal(OrderStatus.Cancelled, saved.Status);
        Assert.Equal(0, await context.OrderShares.CountAsync(link => link.OrderId == order.Id));
    }

    [Fact]
    public async Task ManualImpactRejectsAnOutOfRangePercent()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();

        var request = new ManualNewsRequest(NewsImpactScope.Company, "ufo", NewsImpactDirection.Increase, 150m, company.Id, null);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(0, await context.NewsPosts.CountAsync());
    }

    [Fact]
    public async Task ManualImpactRejectsAnUnknownCompany()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        var request = new ManualNewsRequest(NewsImpactScope.Company, "ufo", NewsImpactDirection.Increase, 5m, 999999, null);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);

        Assert.False(result.Success);
        Assert.Equal(0, await context.NewsPosts.CountAsync());
    }

    [Fact]
    public async Task ManualImpactRejectsAnUnknownTheme()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();

        var request = new ManualNewsRequest(NewsImpactScope.Company, "not-a-theme", NewsImpactDirection.Increase, 5m, company.Id, null);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);

        Assert.False(result.Success);
        Assert.Equal(0, await context.NewsPosts.CountAsync());
    }

    [Fact]
    public async Task AutomatedNewsIsSkippedOnANonScheduledCycle()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var cycle = await context.MarketCycles.FirstAsync();

        var settings = new NewsOptions { Enabled = true, CyclesBetweenPosts = 25 };
        // Cycle 1 is not a multiple of 25, so it returns before drawing; an empty script would throw if drawn.
        var result = await Service(settings, new ScriptedRandom([], []))
            .MaybeAddAutomatedNewsForCycleAsync(cycle, DateTime.UtcNow);

        Assert.False(result.Published);
    }

    [Fact]
    public async Task AutomatedNewsIsSkippedWhenDisabled()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var cycle = await context.MarketCycles.FirstAsync();
        cycle.CycleNumber = 25;
        await context.SaveChangesAsync();

        var settings = new NewsOptions { Enabled = false, CyclesBetweenPosts = 25 };
        var result = await Service(settings, new ScriptedRandom([], []))
            .MaybeAddAutomatedNewsForCycleAsync(cycle, DateTime.UtcNow);

        Assert.False(result.Published);
    }

    [Fact]
    public async Task AutomatedNewsPublishesOnTheScheduledCycle()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var cycle = await context.MarketCycles.FirstAsync();
        cycle.CycleNumber = 25;
        await context.SaveChangesAsync();

        var settings = new NewsOptions { Enabled = true, CyclesBetweenPosts = 25, ImpactProbability = 0.0 };
        // 4 ints for the content draw; one double for the impact gate (0 is not < 0.0, so no impact).
        var random = new ScriptedRandom([0d], [0, 0, 0, 0]);
        var result = await Service(settings, random).MaybeAddAutomatedNewsForCycleAsync(cycle, DateTime.UtcNow);

        Assert.True(result.Published);
        Assert.Equal(NewsImpactScope.None, result.Post!.Scope);

        // The automated path adds without saving; the cycle advance owns the save in production.
        await context.SaveChangesAsync();
        Assert.Equal(1, await context.NewsPosts.CountAsync());
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
