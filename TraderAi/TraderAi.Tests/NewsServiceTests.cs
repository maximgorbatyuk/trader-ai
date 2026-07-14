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
    private const string FinanceTheme = "market-sentiment";

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

    private NewsService Service(
        NewsOptions settings,
        Random random,
        RandomChanceRatesOptions? chanceRates = null,
        IndustrySentimentOptions? sentimentOptions = null) =>
        new(
            context,
            new MarketCycleLock(),
            Options.Create(settings),
            Options.Create(chanceRates ?? new RandomChanceRatesOptions()),
            new MarketImpactService(context),
            random,
            Options.Create(sentimentOptions ?? new IndustrySentimentOptions { Enabled = true }));

    private async Task<decimal> LatestPriceAsync(int companyId) =>
        await context.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == companyId)
            .OrderByDescending(snapshot => snapshot.Id)
            .Select(snapshot => snapshot.Price)
            .FirstAsync();

    [Fact]
    public async Task ManualCompanyNewsDefersItsPriceImpactUntilTheCycleApplyPass()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();

        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(
            new ManualNewsRequest(NewsImpactScope.Company, FinanceTheme, NewsImpactDirection.Increase, 5m, company.Id, null));

        Assert.True(result.Success);
        Assert.Equal(100m, await LatestPriceAsync(company.Id));
        Assert.Null(result.Post!.ImpactAppliedInCycleId);
    }

    [Fact]
    public async Task ApplyPassMovesCompanyNewsTargetAndPeerThenStampsThePost()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var target = await context.Companies.FirstAsync();
        var cycle = await context.MarketCycles.FirstAsync();
        var now = DateTime.UtcNow;
        var peer = new Company { Name = "Peer", IndustryId = target.IndustryId, IssuedSharesCount = 10, CreatedAt = now, UpdatedAt = now };
        context.Companies.Add(peer);
        await context.SaveChangesAsync();
        context.PriceSnapshots.Add(new PriceSnapshot { CompanyId = peer.Id, Price = 200m, CreatedInCycleId = cycle.Id, CreatedAt = now });
        await context.SaveChangesAsync();

        var service = Service(new NewsOptions(), new Random(1));
        var result = await service.PublishManualNewsAsync(
            new ManualNewsRequest(NewsImpactScope.Company, FinanceTheme, NewsImpactDirection.Increase, 8m, target.Id, null));
        var moved = await service.ApplyPendingImpactsForCycleAsync(cycle, now);
        await context.SaveChangesAsync();

        Assert.True(result.Success);
        Assert.Equal(2, moved);
        Assert.Equal(108m, await LatestPriceAsync(target.Id));
        Assert.Equal(204m, await LatestPriceAsync(peer.Id));
        Assert.Equal(cycle.Id, (await context.NewsPosts.SingleAsync()).ImpactAppliedInCycleId);
    }

    [Fact]
    public async Task ApplyPassNudgesEachIndustryNewsTargetOnceAndClampsIt()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var cycle = await context.MarketCycles.FirstAsync();
        var industry = await context.Industries.FirstAsync();
        industry.SentimentValue = 98;
        await context.SaveChangesAsync();

        var service = Service(new NewsOptions(), new Random(1), sentimentOptions: new IndustrySentimentOptions { Enabled = true, SentimentValueLimit = 100 });
        var result = await service.PublishManualNewsAsync(
            new ManualNewsRequest(NewsImpactScope.Industries, FinanceTheme, NewsImpactDirection.Increase, 5m, null, [industry.Id]));
        await service.ApplyPendingImpactsForCycleAsync(cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.True(result.Success);
        Assert.Equal(105.49m, await LatestPriceAsync((await context.Companies.FirstAsync()).Id));
        Assert.Equal(100, (await context.Industries.SingleAsync()).SentimentValue);
        Assert.Equal(cycle.Id, (await context.NewsPosts.SingleAsync()).ImpactAppliedInCycleId);
    }

    [Fact]
    public async Task DisabledSentimentAppliesBaseScopedNewsImpactsWithoutNudgingIndustries()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var target = await context.Companies.FirstAsync();
        var industry = await context.Industries.FirstAsync();
        var cycle = await context.MarketCycles.FirstAsync();
        var now = DateTime.UtcNow;
        industry.SentimentValue = 500;
        industry.SectorBeta = 2m;
        var secondIndustry = new Industry { Name = "Second", SentimentValue = -500, SectorBeta = 2m };
        context.Industries.Add(secondIndustry);
        await context.SaveChangesAsync();
        var secondCompany = new Company { Name = "Second company", IndustryId = secondIndustry.Id, IssuedSharesCount = 10, CreatedAt = now, UpdatedAt = now };
        context.Companies.Add(secondCompany);
        await context.SaveChangesAsync();
        context.PriceSnapshots.Add(new PriceSnapshot { CompanyId = secondCompany.Id, Price = 200m, CreatedInCycleId = cycle.Id, CreatedAt = now });
        await context.SaveChangesAsync();

        var service = Service(
            new NewsOptions(),
            new Random(1),
            sentimentOptions: new IndustrySentimentOptions { Enabled = false });
        Assert.True((await service.PublishManualNewsAsync(
            new ManualNewsRequest(NewsImpactScope.Company, FinanceTheme, NewsImpactDirection.Increase, 8m, target.Id, null))).Success);
        Assert.True((await service.PublishManualNewsAsync(
            new ManualNewsRequest(NewsImpactScope.Industries, FinanceTheme, NewsImpactDirection.Decrease, 5m, null, [secondIndustry.Id]))).Success);

        await service.ApplyPendingImpactsForCycleAsync(cycle, now);
        await context.SaveChangesAsync();

        Assert.Equal(108m, await LatestPriceAsync(target.Id));
        Assert.Equal(190m, await LatestPriceAsync(secondCompany.Id));
        Assert.Equal(500, industry.SentimentValue);
        Assert.Equal(-500, secondIndustry.SentimentValue);
    }

    [Fact]
    public async Task ApplyPassSkipsScopeNoneAndHistoricalUnmarkedPosts()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var current = await context.MarketCycles.FirstAsync();
        var earlier = new MarketCycle { CycleNumber = 0, Status = CycleStatus.Completed, StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow };
        context.MarketCycles.Add(earlier);
        var company = await context.Companies.FirstAsync();
        context.NewsPosts.AddRange(
            new NewsPost { Title = "Flavour", Content = "Body", PublishedInCycleId = current.Id, PublishedAt = DateTime.UtcNow, Scope = NewsImpactScope.None },
            new NewsPost { Title = "Old", Content = "Body", PublishedInCycleId = earlier.Id, PublishedAt = DateTime.UtcNow, Scope = NewsImpactScope.Company, Direction = NewsImpactDirection.Increase, ImpactPercent = 5m, TargetCompanyId = company.Id });
        await context.SaveChangesAsync();

        var moved = await Service(new NewsOptions(), new Random(1)).ApplyPendingImpactsForCycleAsync(current, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, moved);
        Assert.Equal(100m, await LatestPriceAsync(company.Id));
        Assert.All(await context.NewsPosts.ToListAsync(), post => Assert.Null(post.ImpactAppliedInCycleId));
    }

    [Fact]
    public async Task ApplyPassMarksAMissingCompanyTargetWithoutRetryingIt()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var cycle = await context.MarketCycles.FirstAsync();
        context.NewsPosts.Add(new NewsPost
        {
            Title = "Missing target",
            Content = "Body",
            PublishedInCycleId = cycle.Id,
            PublishedAt = DateTime.UtcNow,
            Scope = NewsImpactScope.Company,
            Direction = NewsImpactDirection.Decrease,
            ImpactPercent = 5m,
            TargetCompanyId = 999_999,
        });
        await context.SaveChangesAsync();

        var moved = await Service(new NewsOptions(), new Random(1)).ApplyPendingImpactsForCycleAsync(cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, moved);
        Assert.Equal(cycle.Id, (await context.NewsPosts.SingleAsync()).ImpactAppliedInCycleId);
        Assert.Equal(1, await context.PriceSnapshots.CountAsync());
    }

    [Fact]
    public async Task AutomatedScopedNewsDefersTheSameWayAsManualNews()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var cycle = await context.MarketCycles.FirstAsync();
        cycle.CycleNumber = 25;
        await context.SaveChangesAsync();
        var company = await context.Companies.FirstAsync();
        var service = Service(
            new NewsOptions { Enabled = true, CyclesBetweenPosts = 25 },
            new ScriptedRandom([0d, 0d, 0d], [0, 0, 0, 0, 0, 0]),
            new RandomChanceRatesOptions { EventTriggerChances = { NewsImpact = 1.0, NewsCompanyScope = 1.0 } });

        var published = await service.MaybeAddAutomatedNewsForCycleAsync(cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(NewsImpactScope.Company, published.Post!.Scope);
        Assert.Equal(0, published.CompaniesMoved);
        Assert.Equal(100m, await LatestPriceAsync(company.Id));
        Assert.Null(published.Post.ImpactAppliedInCycleId);
        await service.ApplyPendingImpactsForCycleAsync(cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();
        Assert.Equal(100.1m, await LatestPriceAsync(company.Id));
    }

    [Fact]
    public async Task MarketAdvanceMatchesBeforeApplyingNewsAndLeavesTheNewPriceForTheNextDecisionCycle()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var seller = await context.Participants.FirstAsync(participant => participant.Name == "Alice");
        var buyer = await context.Participants.FirstAsync(participant => participant.Name == "Bob");
        var firstCycleId = (await context.Markets.SingleAsync()).CurrentCycleId!.Value;
        var news = Service(new NewsOptions(), new Random(1));
        var decisions = new PriceObservingDecisionEngine();
        var market = new MarketService(
            context,
            new MatchingEngine(context),
            decisions,
            new MarketCycleLock(),
            new Random(1),
            newsService: news);

        Assert.True((await market.PlaceOrderAsync(seller.Id, company.Id, OrderType.Sell, 1, 100m)).Success);
        Assert.True((await market.PlaceOrderAsync(buyer.Id, company.Id, OrderType.Buy, 1, 100m)).Success);
        Assert.True((await news.PublishManualNewsAsync(
            new ManualNewsRequest(NewsImpactScope.Company, FinanceTheme, NewsImpactDirection.Increase, 5m, company.Id, null))).Success);

        var tick = await market.RunCycleTickAsync();
        await market.RunCycleTickAsync();

        Assert.Equal(1, tick.FillCount);
        Assert.Equal(105m, await LatestPriceAsync(company.Id));
        Assert.Equal([100m, 100m, 105m, 105m], decisions.ObservedPrices);
        Assert.Equal(firstCycleId, (await context.NewsPosts.SingleAsync()).ImpactAppliedInCycleId);
    }

    [Fact]
    public async Task ManualCompanyImpactSnapshotsTheTargetAtTheMovedPrice()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();

        var request = new ManualNewsRequest(NewsImpactScope.Company, FinanceTheme, NewsImpactDirection.Increase, 5m, company.Id, null);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);

        Assert.True(result.Success);
        var post = result.Post!;
        Assert.Equal(NewsImpactScope.Company, post.Scope);
        Assert.Equal(NewsImpactDirection.Increase, post.Direction);
        Assert.Equal(5m, post.ImpactPercent);
        Assert.Equal(company.Id, post.TargetCompanyId);

        var cycle = await context.MarketCycles.FirstAsync();
        await Service(new NewsOptions(), new Random(1)).ApplyPendingImpactsForCycleAsync(cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // Seed price 100 moved up 5% lands at 105 and becomes the company's latest snapshot.
        var latest = await context.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync();
        Assert.Equal(105m, latest.Price);
    }

    [Fact]
    public async Task CompanyImpactRipplesToIndustryPeersAtAQuarterStrength()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var target = await context.Companies.FirstAsync();
        var targetIndustry = await context.Industries.FirstAsync(industry => industry.Id == target.IndustryId);
        targetIndustry.SentimentValue = 500;
        targetIndustry.SectorBeta = 2m;
        var cycle = await context.MarketCycles.FirstAsync();
        var now = DateTime.UtcNow;

        // A peer in the target's industry (should move a quarter as much) and a company in another industry
        // (should not move at all).
        var otherIndustry = new Industry { Name = "Healthcare" };
        context.Industries.Add(otherIndustry);
        await context.SaveChangesAsync();
        var peer = new Company { Name = "Beta Corp", IndustryId = target.IndustryId, IssuedSharesCount = 10, CreatedAt = now, UpdatedAt = now };
        var outsider = new Company { Name = "Gamma Corp", IndustryId = otherIndustry.Id, IssuedSharesCount = 10, CreatedAt = now, UpdatedAt = now };
        context.Companies.AddRange(peer, outsider);
        await context.SaveChangesAsync();
        context.PriceSnapshots.Add(new PriceSnapshot { CompanyId = peer.Id, Price = 200m, CreatedInCycleId = cycle.Id, CreatedAt = now });
        context.PriceSnapshots.Add(new PriceSnapshot { CompanyId = outsider.Id, Price = 300m, CreatedInCycleId = cycle.Id, CreatedAt = now });
        await context.SaveChangesAsync();

        var request = new ManualNewsRequest(NewsImpactScope.Company, FinanceTheme, NewsImpactDirection.Increase, 8m, target.Id, null);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);
        await Service(new NewsOptions(), new Random(1)).ApplyPendingImpactsForCycleAsync(cycle, now);
        await context.SaveChangesAsync();

        Assert.True(result.Success);
        Assert.Equal(124m, await LatestPriceAsync(target.Id));
        Assert.Equal(212m, await LatestPriceAsync(peer.Id));
        Assert.Equal(300m, await LatestPriceAsync(outsider.Id)); // different industry, untouched
    }

    [Fact]
    public async Task ManualIndustriesImpactMovesEveryCompanyInTheChosenIndustries()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();
        var industry = await context.Industries.FirstAsync();
        industry.SentimentValue = -500;
        industry.SectorBeta = 2m;
        await context.SaveChangesAsync();

        var request = new ManualNewsRequest(
            NewsImpactScope.Industries, FinanceTheme, NewsImpactDirection.Decrease, 5m, null, [industry.Id]);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);
        var cycle = await context.MarketCycles.FirstAsync();
        await Service(new NewsOptions(), new Random(1)).ApplyPendingImpactsForCycleAsync(cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

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
        Assert.Equal(85m, latest.Price);
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

        var request = new ManualNewsRequest(NewsImpactScope.Company, FinanceTheme, NewsImpactDirection.Decrease, 10m, company.Id, null);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);
        await Service(new NewsOptions(), new Random(1)).ApplyPendingImpactsForCycleAsync(cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

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

        context.Orders.Add(order);
        await context.SaveChangesAsync();

        var request = new ManualNewsRequest(NewsImpactScope.Company, FinanceTheme, NewsImpactDirection.Increase, 10m, company.Id, null);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);
        await Service(new NewsOptions(), new Random(1)).ApplyPendingImpactsForCycleAsync(cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.True(result.Success);
        var saved = await context.Orders.AsNoTracking().FirstAsync(saved => saved.Id == order.Id);
        Assert.Equal(OrderStatus.Cancelled, saved.Status);
        // Cancelling the stale sell frees the seller's shares; the whole position is theirs again.
        Assert.Equal(10, await context.Holdings.Where(holding => holding.ParticipantId == seller.Id).SumAsync(holding => holding.Quantity));
    }

    [Fact]
    public async Task ManualImpactRejectsAnOutOfRangePercent()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();

        var request = new ManualNewsRequest(NewsImpactScope.Company, FinanceTheme, NewsImpactDirection.Increase, 150m, company.Id, null);
        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(request);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(0, await context.NewsPosts.CountAsync());
    }

    [Fact]
    public async Task ManualImpactRejectsAnUnknownCompany()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);

        var request = new ManualNewsRequest(NewsImpactScope.Company, FinanceTheme, NewsImpactDirection.Increase, 5m, 999999, null);
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
    public async Task ManualImpactRejectsAnInvalidDirectionBeforeGeneratingOrPersistingNews()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();

        var result = await Service(new NewsOptions(), new ScriptedRandom([], [])).PublishManualNewsAsync(
            new ManualNewsRequest(NewsImpactScope.Company, FinanceTheme, (NewsImpactDirection)99, 5m, company.Id, null));

        Assert.False(result.Success);
        Assert.Equal("Direction must be Increase or Decrease.", result.Error);
        Assert.Equal(0, await context.NewsPosts.CountAsync());
        Assert.Equal(100m, await LatestPriceAsync(company.Id));
    }

    [Fact]
    public async Task ManualImpactRejectsAWhimsicalThemeForAScopedPost()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();

        var result = await Service(new NewsOptions(), new Random(1)).PublishManualNewsAsync(
            new ManualNewsRequest(NewsImpactScope.Company, "ufo", NewsImpactDirection.Increase, 5m, company.Id, null));

        Assert.False(result.Success);
        Assert.Equal("Unknown finance theme.", result.Error);
        Assert.Equal(0, await context.NewsPosts.CountAsync());
    }

    [Fact]
    public async Task ManualFinanceThemeUsesBullishSentimentLanguageForAnIncrease()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var company = await context.Companies.FirstAsync();

        var result = await Service(new NewsOptions(), new ScriptedRandom([], [0])).PublishManualNewsAsync(
            new ManualNewsRequest(NewsImpactScope.Company, FinanceTheme, NewsImpactDirection.Increase, 5m, company.Id, null));

        Assert.True(result.Success);
        var wording = $"{result.Post!.Title} {result.Post.Content}".ToLowerInvariant();
        Assert.Contains("sentiment", wording);
        Assert.Contains("rally", wording);
        Assert.DoesNotContain("selloff", wording);
    }

    [Fact]
    public async Task AutomatedCompanyIncreaseUsesBullishFinanceSentimentLanguage()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var cycle = await context.MarketCycles.FirstAsync();
        cycle.CycleNumber = 25;
        await context.SaveChangesAsync();

        var result = await Service(
            new NewsOptions { Enabled = true, CyclesBetweenPosts = 25 },
            // Generic content keeps its original four shared draws before direction and target selection.
            new ScriptedRandom([0d, 0d, 0d], [0, 0, 0, 0, 0, 0]),
            new RandomChanceRatesOptions { EventTriggerChances = { NewsImpact = 1.0, NewsCompanyScope = 1.0 } })
            .MaybeAddAutomatedNewsForCycleAsync(cycle, DateTime.UtcNow);

        var wording = $"{result.Post!.Title} {result.Post.Content}".ToLowerInvariant();
        Assert.Equal(NewsImpactScope.Company, result.Post.Scope);
        Assert.Equal(NewsImpactDirection.Increase, result.Post.Direction);
        Assert.Contains("sentiment", wording);
        Assert.Contains("rally", wording);
        Assert.DoesNotContain("selloff", wording);
    }

    [Fact]
    public async Task AutomatedCompanyDecreaseUsesBearishFinanceSentimentLanguage()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var cycle = await context.MarketCycles.FirstAsync();
        cycle.CycleNumber = 25;
        await context.SaveChangesAsync();

        var result = await Service(
            new NewsOptions { Enabled = true, CyclesBetweenPosts = 25 },
            new ScriptedRandom([0d, 0d, 0d], [0, 0, 0, 0, 1, 0]),
            new RandomChanceRatesOptions { EventTriggerChances = { NewsImpact = 1.0, NewsCompanyScope = 1.0 } })
            .MaybeAddAutomatedNewsForCycleAsync(cycle, DateTime.UtcNow);

        var wording = $"{result.Post!.Title} {result.Post.Content}".ToLowerInvariant();
        Assert.Equal(NewsImpactScope.Company, result.Post.Scope);
        Assert.Equal(NewsImpactDirection.Decrease, result.Post.Direction);
        Assert.Contains("sentiment", wording);
        Assert.Contains("selloff", wording);
        Assert.DoesNotContain("rally", wording);
    }

    [Fact]
    public async Task AutomatedScopedNewsKeepsTheOriginalSharedRandomDrawOrder()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var cycle = await context.MarketCycles.FirstAsync();
        cycle.CycleNumber = 25;
        await context.SaveChangesAsync();
        var random = new RecordingRandom([0d, 0d, 0d], [0, 0, 0, 0, 0, 0]);

        var result = await Service(
            new NewsOptions { Enabled = true, CyclesBetweenPosts = 25 },
            random,
            new RandomChanceRatesOptions { EventTriggerChances = { NewsImpact = 1.0, NewsCompanyScope = 1.0 } })
            .MaybeAddAutomatedNewsForCycleAsync(cycle, DateTime.UtcNow);

        Assert.Equal(NewsImpactScope.Company, result.Post!.Scope);
        Assert.Contains("sentiment", $"{result.Post.Title} {result.Post.Content}".ToLowerInvariant());
        Assert.Equal(["N:10", "N:8", "N:8", "N:3", "D", "N:2", "D", "D", "N:1"], random.Calls);
    }

    [Fact]
    public async Task AutomatedImpactFreeNewsKeepsTheOriginalSharedRandomDrawOrder()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var cycle = await context.MarketCycles.FirstAsync();
        cycle.CycleNumber = 25;
        await context.SaveChangesAsync();
        var random = new RecordingRandom([0d], [0, 0, 0, 0]);

        var result = await Service(
            new NewsOptions { Enabled = true, CyclesBetweenPosts = 25 },
            random,
            new RandomChanceRatesOptions { EventTriggerChances = { NewsImpact = 0.0 } })
            .MaybeAddAutomatedNewsForCycleAsync(cycle, DateTime.UtcNow);

        Assert.Equal(NewsImpactScope.None, result.Post!.Scope);
        Assert.Contains("UFO", result.Post.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["N:10", "N:8", "N:8", "N:3", "D"], random.Calls);
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

        var settings = new NewsOptions { Enabled = true, CyclesBetweenPosts = 25 };
        var chanceRates = new RandomChanceRatesOptions { EventTriggerChances = { NewsImpact = 0.0 } };
        // The full whimsical content draw stays ahead of the impact gate; 0 is not < 0.0, so no impact.
        var random = new ScriptedRandom([0d], [0, 0, 0, 0]);
        var result = await Service(settings, random, chanceRates).MaybeAddAutomatedNewsForCycleAsync(cycle, DateTime.UtcNow);

        Assert.True(result.Published);
        Assert.Equal(NewsImpactScope.None, result.Post!.Scope);
        Assert.Contains("UFO", result.Post.Title, StringComparison.OrdinalIgnoreCase);

        // The automated path adds without saving; the cycle advance owns the save in production.
        await context.SaveChangesAsync();
        Assert.Equal(1, await context.NewsPosts.CountAsync());
    }

    [Fact]
    public async Task DuringACrisisAPriceLiftingAutomatedPostIsSuppressed()
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var cycle = await context.MarketCycles.FirstAsync();
        cycle.CycleNumber = 25;
        await context.SaveChangesAsync();
        var company = await context.Companies.FirstAsync();
        var priceBefore = await LatestPriceAsync(company.Id);

        var settings = new NewsOptions { Enabled = true, CyclesBetweenPosts = 25 };
        var chanceRates = new RandomChanceRatesOptions { EventTriggerChances = { NewsImpact = 1.0 } };
        // The generic content draws run first, then the Increase direction reaches the crisis suppression roll.
        var random = new ScriptedRandom([0d, 0d], [0, 0, 0, 0, 0]);
        var result = await Service(settings, random, chanceRates)
            .MaybeAddAutomatedNewsForCycleAsync(cycle, DateTime.UtcNow, duringCrisis: true);
        await context.SaveChangesAsync();

        Assert.True(result.Published);
        Assert.Equal(0, result.CompaniesMoved);
        Assert.Equal(NewsImpactScope.None, result.Post!.Scope);
        Assert.Equal(priceBefore, await LatestPriceAsync(company.Id));
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

    private sealed class RecordingRandom(double[] doubles, int[] ints) : Random
    {
        private readonly Queue<double> doubles = new(doubles);
        private readonly Queue<int> ints = new(ints);

        public List<string> Calls { get; } = [];

        public override double NextDouble()
        {
            Calls.Add("D");
            return doubles.Dequeue();
        }

        public override int Next(int maxValue)
        {
            Calls.Add($"N:{maxValue}");
            return ints.Dequeue();
        }

        public override int Next(int minValue, int maxValue)
        {
            Calls.Add($"R:{minValue}:{maxValue}");
            return ints.Dequeue();
        }
    }

    private sealed class PriceObservingDecisionEngine : IDecisionEngine
    {
        public List<decimal> ObservedPrices { get; } = [];

        public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
        {
            ObservedPrices.AddRange(context.Companies.Select(company => company.Price));
            return [];
        }
    }
}
