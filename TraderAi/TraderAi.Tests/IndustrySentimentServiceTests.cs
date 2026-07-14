using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class IndustrySentimentServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public IndustrySentimentServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task NeutralIndustriesCanWalkUpDownOrHold()
    {
        var currentCycle = await SeedCyclesAsync();
        context.Industries.AddRange(
            new Industry { Name = "Up" },
            new Industry { Name = "Down" },
            new Industry { Name = "Hold" });
        await context.SaveChangesAsync();

        var random = new ScriptedRandom([0.10d, 0.30d, 0.90d]);
        await Service(random).ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber);

        var values = await context.Industries
            .OrderBy(industry => industry.Id)
            .Select(industry => industry.SentimentValue)
            .ToArrayAsync();

        Assert.Equal([4, -4, 0], values);
        Assert.Equal(3, random.DrawCount);
    }

    [Fact]
    public async Task VolatilityRaisesBothRevisionProbabilities()
    {
        var currentCycle = await SeedCyclesAsync();
        context.Industries.Add(new Industry { Name = "Cyclical", SentimentVolatility = 3m });
        await context.SaveChangesAsync();

        await Service(new ScriptedRandom([0.30d])).ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber);

        Assert.Equal(4, await context.Industries.Select(industry => industry.SentimentValue).SingleAsync());
    }

    [Fact]
    public async Task RevisionDampsTowardBothSentimentLimits()
    {
        var currentCycle = await SeedCyclesAsync();
        context.Industries.AddRange(
            new Industry { Name = "Near upper limit", SentimentValue = 900 },
            new Industry { Name = "Near lower limit", SentimentValue = -900 });
        await context.SaveChangesAsync();

        await Service(new ScriptedRandom([0.03d, 0.30d]))
            .ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber);

        var values = await context.Industries
            .OrderBy(industry => industry.Id)
            .Select(industry => industry.SentimentValue)
            .ToArrayAsync();
        Assert.Equal([894, -899], values);
    }

    [Fact]
    public async Task ThresholdsAndFinalValuesStayWithinTheirLimits()
    {
        var currentCycle = await SeedCyclesAsync();
        context.Industries.AddRange(
            new Industry { Name = "Cumulative chance", SentimentValue = 0 },
            new Industry { Name = "Above upper limit", SentimentValue = 2_000 },
            new Industry { Name = "Below lower limit", SentimentValue = -2_000 });
        await context.SaveChangesAsync();

        await Service(new ScriptedRandom([0.90d, 0.99d, 0.10d]), revisionBase: 0.75)
            .ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber);

        var values = await context.Industries
            .OrderBy(industry => industry.Id)
            .Select(industry => industry.SentimentValue)
            .ToArrayAsync();
        Assert.Equal([-4, 1_000, -1_000], values);
    }

    [Fact]
    public async Task DecayMeanRevertsWithoutOvershootingZero()
    {
        var currentCycle = await SeedCyclesAsync();
        context.Industries.AddRange(
            new Industry { Name = "Positive", SentimentValue = 3 },
            new Industry { Name = "Negative", SentimentValue = -3 });
        await context.SaveChangesAsync();

        await Service(new ScriptedRandom([0.50d, 0.50d]), revisionBase: 0d, decay: 5)
            .ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber);

        Assert.All(await context.Industries.ToArrayAsync(), industry => Assert.Equal(0, industry.SentimentValue));
    }

    [Fact]
    public async Task InvalidLimitAndDecayRemainDeterministicAndStillConsumeTheDraw()
    {
        var currentCycle = await SeedCyclesAsync();
        context.Industries.Add(new Industry { Name = "Invalid options", SentimentValue = 100 });
        await context.SaveChangesAsync();
        var random = new ScriptedRandom([0.50d]);

        await Service(random, limit: 0, decay: -5)
            .ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber);

        Assert.Equal(0, await context.Industries.Select(industry => industry.SentimentValue).SingleAsync());
        Assert.Equal(1, random.DrawCount);
    }

    [Fact]
    public async Task ActiveCrisisMakesEveryIndustryRiskOff()
    {
        var currentCycle = await SeedCyclesAsync();
        context.Industries.Add(new Industry { Name = "Unaffected sector", SentimentValue = 10 });
        await context.SaveChangesAsync();
        var crisis = await AddCrisisAsync(currentCycle);

        await Service(new ScriptedRandom([0.0d]))
            .ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber, crisis);

        Assert.Equal(4, await context.Industries.Select(industry => industry.SentimentValue).SingleAsync());
    }

    [Fact]
    public async Task CrisisHitAddsItsSectorPushOnTopOfTheWalk()
    {
        var currentCycle = await SeedCyclesAsync();
        var industry = new Industry { Name = "Hit sector" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();
        var crisis = await AddCrisisAsync(currentCycle, industry.Id);

        await Service(new ScriptedRandom([0.0d]))
            .ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber, crisis);

        Assert.Equal(-19, await context.Industries.Select(item => item.SentimentValue).SingleAsync());
    }

    [Theory]
    [InlineData(NewsImpactDirection.Increase, 0.30d, 4)]
    [InlineData(NewsImpactDirection.Decrease, 0.55d, -4)]
    public async Task PriorCycleCompanyNewsBiasesItsIndustry(
        NewsImpactDirection direction,
        double roll,
        int expected)
    {
        var currentCycle = await SeedCyclesAsync();
        var industry = new Industry { Name = "News sector" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();
        await AddCompanyNewsAsync(currentCycle, industry, direction);

        await Service(new ScriptedRandom([roll]))
            .ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber);

        Assert.Equal(expected, await context.Industries.Select(item => item.SentimentValue).SingleAsync());
    }

    [Fact]
    public async Task MultipleSameDirectionPostsAddOnlyOneBonus()
    {
        var currentCycle = await SeedCyclesAsync();
        var industry = new Industry { Name = "Busy news sector" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();
        await AddCompanyNewsAsync(
            currentCycle,
            industry,
            NewsImpactDirection.Increase,
            NewsImpactDirection.Increase);

        await Service(new ScriptedRandom([0.36d]))
            .ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber);

        Assert.Equal(-4, await context.Industries.Select(item => item.SentimentValue).SingleAsync());
    }

    [Fact]
    public async Task MixedCompanyNewsDirectionsCancelTheirBonus()
    {
        var currentCycle = await SeedCyclesAsync();
        var industry = new Industry { Name = "Mixed news sector" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();
        await AddCompanyNewsAsync(
            currentCycle,
            industry,
            NewsImpactDirection.Increase,
            NewsImpactDirection.Decrease);

        await Service(new ScriptedRandom([0.30d]))
            .ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber);

        Assert.Equal(-4, await context.Industries.Select(item => item.SentimentValue).SingleAsync());
    }

    [Fact]
    public async Task CrisisSuppressesPositiveNewsButKeepsNegativeNewsRiskOff()
    {
        var currentCycle = await SeedCyclesAsync();
        var positive = new Industry { Name = "Positive news" };
        var negative = new Industry { Name = "Negative news" };
        context.Industries.AddRange(positive, negative);
        await context.SaveChangesAsync();
        await AddCompanyNewsAsync(currentCycle, positive, NewsImpactDirection.Increase);
        await AddCompanyNewsAsync(currentCycle, negative, NewsImpactDirection.Decrease);
        var crisis = await AddCrisisAsync(currentCycle);

        await Service(new ScriptedRandom([0.05d, 0.05d]), crisisForcedDown: 0d)
            .ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber, crisis);

        var values = await context.Industries
            .OrderBy(industry => industry.Id)
            .Select(industry => industry.SentimentValue)
            .ToArrayAsync();
        Assert.Equal([0, -4], values);
    }

    [Fact]
    public async Task DisabledServiceDoesNotDrawOrChangeSentiment()
    {
        var currentCycle = await SeedCyclesAsync();
        context.Industries.Add(new Industry { Name = "Disabled", SentimentValue = 10 });
        await context.SaveChangesAsync();
        var random = new ScriptedRandom([]);

        await Service(random, enabled: false)
            .ProcessForCycleAsync(currentCycle.Id, currentCycle.CycleNumber);

        Assert.Equal(10, await context.Industries.Select(item => item.SentimentValue).SingleAsync());
        Assert.Equal(0, random.DrawCount);
    }

    [Fact]
    public async Task MarketStepProcessesSentimentBeforeTheDecisionPass()
    {
        var currentCycle = await SeedCyclesAsync();
        var market = new Market
        {
            Name = "Sentiment market",
            Status = MarketStatus.Running,
            CurrentCycleId = currentCycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Markets.Add(market);
        var industry = new Industry { Name = "Walked sector" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();

        var company = new Company
        {
            Name = "Observed company",
            IndustryId = industry.Id,
            IssuedSharesCount = 100,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var participant = new Participant
        {
            Name = "Observer target",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 1_000m,
            CurrentBalance = 1_000m,
            IsActive = true,
        };
        context.Companies.Add(company);
        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = 100m,
            CreatedInCycleId = currentCycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var sentimentService = Service(new ScriptedRandom([0d]));
        var decisionEngine = new SentimentObservingDecisionEngine(() =>
            context.Industries.AsNoTracking().Single().SentimentValue);
        var marketService = new MarketService(
            context,
            new MatchingEngine(context),
            decisionEngine,
            new MarketCycleLock(),
            new Random(1),
            industrySentimentService: sentimentService);

        await marketService.RunCycleTickAsync();

        Assert.Equal(4, decisionEngine.ObservedSentiment);
        Assert.Equal(4, await context.Industries.Select(industry => industry.SentimentValue).SingleAsync());
    }

    [Fact]
    public async Task CurrentCycleCompanyNewsFeedsTheNextCycleIndustrySentimentBonus()
    {
        var currentCycle = await SeedCyclesAsync();
        context.Markets.Add(new Market
        {
            Name = "News sentiment market",
            Status = MarketStatus.Running,
            CurrentCycleId = currentCycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var industry = new Industry { Name = "Headline sector" };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();
        var company = new Company
        {
            Name = "Headline company",
            IndustryId = industry.Id,
            IssuedSharesCount = 100,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = 100m,
            CreatedInCycleId = currentCycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var sentiment = Service(new ScriptedRandom([0d, 0d]), revisionBase: 0d, newsBonus: 1d);
        var news = new NewsService(
            context,
            new MarketCycleLock(),
            Options.Create(new NewsOptions()),
            Options.Create(new RandomChanceRatesOptions()),
            new MarketImpactService(context),
            new Random(1),
            Options.Create(new IndustrySentimentOptions { SentimentValueLimit = 1_000 }));
        Assert.True((await news.PublishManualNewsAsync(
            new ManualNewsRequest(NewsImpactScope.Company, "market-sentiment", NewsImpactDirection.Increase, 5m, company.Id, null))).Success);
        var market = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            newsService: news,
            industrySentimentService: sentiment);

        await market.RunCycleTickAsync();
        Assert.Equal(currentCycle.Id, (await context.NewsPosts.SingleAsync()).ImpactAppliedInCycleId);
        Assert.Equal(0, await context.Industries.Select(item => item.SentimentValue).SingleAsync());
        await market.RunCycleTickAsync();

        Assert.Equal(4, await context.Industries.Select(item => item.SentimentValue).SingleAsync());
    }

    [Fact]
    public async Task MarketStepSnapshotsEndOfCycleSentimentForEveryIndustry()
    {
        var currentCycle = await SeedCyclesAsync();
        context.Markets.Add(new Market
        {
            Name = "Snapshot market",
            Status = MarketStatus.Running,
            CurrentCycleId = currentCycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        context.Industries.AddRange(
            new Industry { Name = "Positive", SentimentValue = 25 },
            new Industry { Name = "Negative", SentimentValue = -30 });
        await context.SaveChangesAsync();

        var marketService = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1));

        await marketService.RunCycleTickAsync();

        var snapshots = await context.SectorSentimentSnapshots
            .OrderBy(snapshot => snapshot.IndustryId)
            .ToArrayAsync();
        Assert.Equal(2, snapshots.Length);
        Assert.All(snapshots, snapshot => Assert.Equal(currentCycle.Id, snapshot.CreatedInCycleId));
        Assert.Equal([25, -30], snapshots.Select(snapshot => snapshot.SentimentValue));
        Assert.All(snapshots, snapshot => Assert.NotEqual(default, snapshot.CreatedAt));
    }

    [Fact]
    public async Task SharedRetentionArchivesSentimentSnapshotsWithOriginalIds()
    {
        var currentCycle = await SeedCyclesAsync();
        context.Markets.Add(new Market
        {
            Name = "Archive market",
            Status = MarketStatus.Running,
            CurrentCycleId = currentCycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        context.Industries.Add(new Industry { Name = "Archived sector", SentimentValue = 42 });
        await context.SaveChangesAsync();

        var marketService = new MarketService(
            context,
            new MatchingEngine(context),
            new NoOpDecisionEngine(),
            new MarketCycleLock(),
            new Random(1),
            archiveOptions: Options.Create(new ArchiveOptions { Enabled = true, RetentionCycles = 1 }));

        await marketService.RunCycleTickAsync();
        var original = await context.SectorSentimentSnapshots.AsNoTracking().SingleAsync();
        await marketService.RunCycleTickAsync();

        var archived = await context.SectorSentimentSnapshotArchives.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, archived.Id);
        Assert.Equal(original.IndustryId, archived.IndustryId);
        Assert.Equal(original.SentimentValue, archived.SentimentValue);
        Assert.Equal(original.CreatedInCycleId, archived.CreatedInCycleId);
        Assert.Equal(original.CreatedAt, archived.CreatedAt);
        Assert.DoesNotContain(
            await context.SectorSentimentSnapshots.AsNoTracking().ToArrayAsync(),
            snapshot => snapshot.Id == original.Id);
    }

    private IndustrySentimentService Service(
        Random random,
        double revisionBase = 0.25,
        double volatilityFactor = 0.10,
        int limit = 1_000,
        int decay = 1,
        double crisisForcedDown = 0.50,
        double newsBonus = 0.10,
        bool enabled = true) =>
        new(
            context,
            Options.Create(new IndustrySentimentOptions
            {
                Enabled = enabled,
                SentimentValueLimit = limit,
                SentimentDecayPerCycle = decay,
            }),
            Options.Create(new RandomChanceRatesOptions
            {
                EventTriggerChances = new EventTriggerChances
                {
                    IndustrySentimentRevisionBase = revisionBase,
                },
                ChanceModifiers = new ChanceModifiers
                {
                    IndustrySentimentVolatilityFactor = volatilityFactor,
                    CrisisSentimentForcedDown = crisisForcedDown,
                    CompanyNewsSentimentBonus = newsBonus,
                },
            }),
            random);

    private async Task<MarketCycle> SeedCyclesAsync()
    {
        var now = DateTime.UtcNow;
        context.MarketCycles.Add(new MarketCycle
        {
            CycleNumber = 1,
            Status = CycleStatus.Completed,
            StartedAt = now,
            CompletedAt = now,
        });
        var currentCycle = new MarketCycle
        {
            CycleNumber = 2,
            Status = CycleStatus.Running,
            StartedAt = now,
        };
        context.MarketCycles.Add(currentCycle);
        await context.SaveChangesAsync();
        return currentCycle;
    }

    private async Task<Crisis> AddCrisisAsync(MarketCycle currentCycle, params int[] hitIndustryIds)
    {
        var previousCycleId = await context.MarketCycles
            .Where(cycle => cycle.CycleNumber == currentCycle.CycleNumber - 1)
            .Select(cycle => cycle.Id)
            .SingleAsync();
        var crisis = new Crisis
        {
            Title = "Shock",
            Content = "Body",
            Scope = CrisisScope.Global,
            TriggeredInCycleId = previousCycleId,
            TriggeredInCycleNumber = currentCycle.CycleNumber - 1,
            DurationCycles = 10,
            TriggeredAt = DateTime.UtcNow,
        };
        foreach (var industryId in hitIndustryIds)
        {
            crisis.Industries.Add(new CrisisIndustry { IndustryId = industryId, ImpactPercent = 10m });
        }

        context.Crises.Add(crisis);
        await context.SaveChangesAsync();
        return crisis;
    }

    private async Task AddCompanyNewsAsync(
        MarketCycle currentCycle,
        Industry industry,
        params NewsImpactDirection[] directions)
    {
        var company = new Company
        {
            Name = $"{industry.Name} Company",
            IndustryId = industry.Id,
            IssuedSharesCount = 100,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();

        var previousCycleId = await context.MarketCycles
            .Where(cycle => cycle.CycleNumber == currentCycle.CycleNumber - 1)
            .Select(cycle => cycle.Id)
            .SingleAsync();
        foreach (var direction in directions)
        {
            context.NewsPosts.Add(new NewsPost
            {
                Title = $"{direction} headline",
                Content = "Body",
                PublishedInCycleId = previousCycleId,
                ImpactAppliedInCycleId = previousCycleId,
                PublishedAt = DateTime.UtcNow,
                Scope = NewsImpactScope.Company,
                Direction = direction,
                ImpactPercent = 5m,
                TargetCompanyId = company.Id,
            });
        }

        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed class ScriptedRandom(IEnumerable<double> values) : Random
    {
        private readonly Queue<double> values = new(values);

        public int DrawCount { get; private set; }

        public override double NextDouble()
        {
            DrawCount++;
            return values.Dequeue();
        }
    }

    private sealed class SentimentObservingDecisionEngine(Func<int> readSentiment) : IDecisionEngine
    {
        public int? ObservedSentiment { get; private set; }

        public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
        {
            ObservedSentiment = readSentiment();
            return [];
        }
    }
}
