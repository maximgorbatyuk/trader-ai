using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the auditor with a scripted Random so company selection, verdict rolls, impact sizes, and buy-order
// revisions are forced. Backfilling auditors draws nothing, and a disabled positive path preserves the legacy
// draw sequence for focused tests.
public sealed class AuditorServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private int industryId;

    public AuditorServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private AuditorService Service(bool enabled, Random random, double raiseExpectationsChance = 0d)
    {
        var chanceRates = new RandomChanceRatesOptions();
        chanceRates.EventTriggerChances.AuditorRaiseExpectationsChance = raiseExpectationsChance;
        return new(
            context,
            Options.Create(new AuditorOptions { Enabled = enabled }),
            Options.Create(chanceRates),
            random,
            new MarketImpactService(context));
    }

    private NewsService DeferredNews() =>
        new(
            context,
            new MarketCycleLock(),
            Options.Create(new NewsOptions()),
            Options.Create(new RandomChanceRatesOptions()),
            new MarketImpactService(context),
            new Random(1));

    [Theory]
    [InlineData(100, 5)]
    [InlineData(40, 2)]
    [InlineData(3, 1)]
    [InlineData(1, 1)]
    public void AuditorCountIsFivePercentRoundedUp(int companyCount, int expected) =>
        Assert.Equal(expected, AuditorService.AuditorCountFor(companyCount));

    [Fact]
    public async Task DisabledDoesNotRateOrCreateAuditors()
    {
        var cycle = await AddCycleAsync(20);
        await SetupMarketAsync(cycle);
        await AddCompanyAsync();

        await Service(enabled: false, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.Auditors.CountAsync());
        Assert.Equal(0, await context.CompanyRatings.CountAsync());
    }

    [Fact]
    public async Task BackfillsAuditorsWhenNoneExistDrawingNothingForCreation()
    {
        var cycle = await AddCycleAsync(20);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(company.Id, price: 100m, cycle);

        // Three companies would need one auditor; here a single company still needs one. The two draws are the
        // company pick and a stable-issue roll that misses, so no auditor-creation draw is consumed.
        await Service(enabled: true, new ScriptedRandom([0.5d, 0.9d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.Auditors.CountAsync());
        var rating = await context.CompanyRatings.AsNoTracking().SingleAsync();
        Assert.Equal(CompanyRiskRating.Low, rating.Rating);
    }

    [Fact]
    public async Task StablePriceYieldsLowRatingWithNoNewsOrPriceMove()
    {
        var cycle = await AddCycleAsync(20);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(company.Id, price: 100m, cycle);
        var auditor = await AddAuditorAsync();

        await Service(enabled: true, new ScriptedRandom([0.5d, 0.9d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var rating = await context.CompanyRatings.AsNoTracking().SingleAsync();
        Assert.Equal(CompanyRiskRating.Low, rating.Rating);
        Assert.Equal(auditor.Id, rating.AuditorId);
        Assert.Null(rating.ImpactPercent);
        Assert.Equal(0, await context.NewsPosts.CountAsync());
        Assert.Equal(1, await context.PriceSnapshots.CountAsync(snapshot => snapshot.CompanyId == company.Id));
    }

    [Fact]
    public async Task BigPriceMoveYieldsHighRatingRevisesBuysAndPostsImpactFreeNews()
    {
        var older = await AddCycleAsync(10);
        var current = await AddCycleAsync(20);
        await SetupMarketAsync(current);
        var company = await AddCompanyAsync();
        // ~7.2% per cycle over ten cycles — comfortably past the 5% big-move line.
        await AddSnapshotAsync(company.Id, price: 100m, older);
        await AddSnapshotAsync(company.Id, price: 200m, current);
        await AddAuditorAsync();
        var buyer = await AddTraderAsync(Temperament.Balanced, RiskProfile.Medium, reserved: 500m);
        var buy = await AddBuyOrderAsync(buyer.Id, company.Id, quantity: 5, price: 100m, reserved: 500m, current);

        // pick, issue roll misses (>= 0.10), cancel roll hits (< 0.50); one int for the headline.
        await Service(enabled: true, new ScriptedRandom([0.9d, 0.9d, 0.1d], [0]))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var rating = await context.CompanyRatings.AsNoTracking().SingleAsync();
        Assert.Equal(CompanyRiskRating.High, rating.Rating);
        Assert.Null(rating.ImpactPercent);

        var refreshedBuy = await context.Orders.AsNoTracking().FirstAsync(order => order.Id == buy.Id);
        Assert.Equal(OrderStatus.Cancelled, refreshedBuy.Status);
        Assert.Equal(0m, refreshedBuy.ReservedCashAmount);
        var refreshedBuyer = await context.Participants.AsNoTracking().FirstAsync(participant => participant.Id == buyer.Id);
        Assert.Equal(0m, refreshedBuyer.ReservedBalance);

        var news = await context.NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal(NewsImpactScope.None, news.Scope);
        // A big move but no discovered issue leaves the price untouched.
        Assert.Equal(0, await context.PriceSnapshots.CountAsync(snapshot => snapshot.CompanyId == company.Id
            && snapshot.CreatedInCycleId == current.Id && snapshot.Price != 200m));
    }

    [Fact]
    public async Task DiscoveredIssueYieldsExtraDropsPriceAndPostsCompanyNews()
    {
        var older = await AddCycleAsync(10);
        var current = await AddCycleAsync(20);
        await SetupMarketAsync(current);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 100m, older);
        await AddSnapshotAsync(company.Id, price: 200m, current);
        await AddAuditorAsync();
        var buyer = await AddTraderAsync(Temperament.Balanced, RiskProfile.Medium, reserved: 500m);
        var buy = await AddBuyOrderAsync(buyer.Id, company.Id, quantity: 5, price: 100m, reserved: 500m, current);

        // pick, issue roll hits (< 0.10), drop size 0.5 → 15%, cancel roll hits (< 0.70); one int for the headline.
        await Service(enabled: true, new ScriptedRandom([0.9d, 0.05d, 0.5d, 0.1d], [0]))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var rating = await context.CompanyRatings.AsNoTracking().SingleAsync();
        Assert.Equal(CompanyRiskRating.Extra, rating.Rating);
        Assert.Equal(15m, rating.ImpactPercent);

        // A fresh snapshot 15% below the last price of 200.
        var latest = await context.PriceSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync();
        Assert.Equal(170m, latest.Price);

        var news = await context.NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal(NewsImpactScope.Company, news.Scope);
        Assert.Equal(NewsImpactDirection.Decrease, news.Direction);
        Assert.Equal(15m, news.ImpactPercent);
        Assert.Equal(company.Id, news.TargetCompanyId);
        Assert.Equal(current.Id, news.ImpactAppliedInCycleId);

        var snapshotsBeforeApply = await context.PriceSnapshots.CountAsync(snapshot => snapshot.CompanyId == company.Id);
        Assert.Equal(0, await DeferredNews().ApplyPendingImpactsForCycleAsync(current, DateTime.UtcNow));
        await context.SaveChangesAsync();
        Assert.Equal(snapshotsBeforeApply, await context.PriceSnapshots.CountAsync(snapshot => snapshot.CompanyId == company.Id));
        Assert.Equal(170m, (await context.PriceSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync()).Price);

        var refreshedBuy = await context.Orders.AsNoTracking().FirstAsync(order => order.Id == buy.Id);
        Assert.Equal(OrderStatus.Cancelled, refreshedBuy.Status);
    }

    [Fact]
    public async Task RaisedExpectationsLiftsPriceAndCancelsOnlyEligibleSellOrders()
    {
        var cycle = await AddCycleAsync(20);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 100m, cycle);
        await AddAuditorAsync();
        var crisis = await AddCrisisAsync(cycle);

        var ordinary = await AddTraderAsync(Temperament.Balanced, RiskProfile.Medium, reserved: 0m);
        var player = await AddTraderAsync(Temperament.Balanced, RiskProfile.Medium, reserved: 0m);
        player.Type = ParticipantType.Player;
        var bankrupt = await AddTraderAsync(Temperament.Balanced, RiskProfile.Medium, reserved: 0m);
        bankrupt.IsBankrupt = true;
        await context.SaveChangesAsync();

        var ordinarySell = await AddSellOrderAsync(
            ordinary.Id, company.Id, quantity: 5, filledQuantity: 2, price: 100m, cycle);
        var playerSell = await AddSellOrderAsync(
            player.Id, company.Id, quantity: 5, filledQuantity: 0, price: 100m, cycle);
        var bankruptSell = await AddSellOrderAsync(
            bankrupt.Id, company.Id, quantity: 5, filledQuantity: 0, price: 100m, cycle);

        // pick, issue miss, positive roll hit, lift midpoint → 10%; one int for the headline.
        await Service(
                enabled: true,
                new ScriptedRandom([0.5d, 0.9d, 0.05d, 0.5d], [0]),
                raiseExpectationsChance: 0.08d)
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow, crisis);
        await context.SaveChangesAsync();

        var rating = await context.CompanyRatings.AsNoTracking().SingleAsync();
        Assert.Equal(CompanyRiskRating.RaisedExpectations, rating.Rating);
        Assert.Equal(10m, rating.ImpactPercent);

        var latest = await context.PriceSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync();
        Assert.Equal(110m, latest.Price);

        Assert.Equal(OrderStatus.Cancelled, (await context.Orders.AsNoTracking().SingleAsync(order => order.Id == ordinarySell.Id)).Status);
        Assert.Equal(OrderStatus.Open, (await context.Orders.AsNoTracking().SingleAsync(order => order.Id == playerSell.Id)).Status);
        Assert.Equal(OrderStatus.Open, (await context.Orders.AsNoTracking().SingleAsync(order => order.Id == bankruptSell.Id)).Status);

        var news = await context.NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal(NewsImpactScope.Company, news.Scope);
        Assert.Equal(NewsImpactDirection.Increase, news.Direction);
        Assert.Equal(10m, news.ImpactPercent);
        Assert.Equal(company.Id, news.TargetCompanyId);
        Assert.Equal(cycle.Id, news.ImpactAppliedInCycleId);
        Assert.Empty(await context.CrisisEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ActiveCrisisTriplesIssueDiscoveryAndLogsToTimeline()
    {
        var cycle = await AddCycleAsync(20);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 100m, cycle);
        await AddAuditorAsync();
        var crisis = await AddCrisisAsync(cycle);

        // A stable company's issue chance is 0.02, tripled to 0.06 by the crisis; a 0.05 roll now hits.
        // pick 0.5, issue 0.05 (hit), drop size 0.5 → 15%; one int for the headline.
        await Service(enabled: true, new ScriptedRandom([0.5d, 0.05d, 0.5d], [0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow, crisis);
        await context.SaveChangesAsync();

        var rating = await context.CompanyRatings.AsNoTracking().SingleAsync();
        Assert.Equal(CompanyRiskRating.Extra, rating.Rating);

        var timelineEvent = await context.CrisisEvents.AsNoTracking()
            .SingleAsync(row => row.Type == CrisisEventType.AuditorRating);
        Assert.Equal(crisis.Id, timelineEvent.CrisisId);
        Assert.Equal(company.Id, timelineEvent.CompanyId);
    }

    [Fact]
    public async Task WithoutACrisisTheSameRollLeavesAStableCompanyLowAndLogsNothing()
    {
        var cycle = await AddCycleAsync(20);
        await SetupMarketAsync(cycle);
        var company = await AddCompanyAsync(issuedShares: 1000);
        await AddSnapshotAsync(company.Id, price: 100m, cycle);
        await AddAuditorAsync();

        // No crisis: the base 0.02 chance is not cleared by a 0.05 roll, so the verdict stays Low.
        await Service(enabled: true, new ScriptedRandom([0.5d, 0.05d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var rating = await context.CompanyRatings.AsNoTracking().SingleAsync();
        Assert.Equal(CompanyRiskRating.Low, rating.Rating);
        Assert.Equal(0, await context.CrisisEvents.CountAsync());
    }

    [Fact]
    public async Task RatedCompanyIsNotReviewedAgainWithinTheSafePeriod()
    {
        var ratedAt = await AddCycleAsync(10);
        var current = await AddCycleAsync(20);
        await SetupMarketAsync(current);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(company.Id, price: 100m, current);
        var auditor = await AddAuditorAsync();
        await AddRatingAsync(company.Id, auditor.Id, CompanyRiskRating.Low, ratedAt);

        // Only five cycles have passed (< 15), so the sole company is ineligible and the auditor draws nothing.
        await Service(enabled: true, new ScriptedRandom([], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.CompanyRatings.CountAsync());
    }

    [Fact]
    public async Task SafePeriodExpiresAfterFifteenCycles()
    {
        var ratedAt = await AddCycleAsync(10);
        var current = await AddCycleAsync(25);
        await SetupMarketAsync(current);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(company.Id, price: 100m, current);
        var auditor = await AddAuditorAsync();
        await AddRatingAsync(company.Id, auditor.Id, CompanyRiskRating.Low, ratedAt);

        // Fifteen cycles on, the company is eligible again: pick then a stable-miss roll produce a second rating.
        await Service(enabled: true, new ScriptedRandom([0.5d, 0.9d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(2, await context.CompanyRatings.CountAsync());
    }

    [Fact]
    public async Task CapitalizationFloorLetsASmallCompanyBeSelected()
    {
        var cycle = await AddCycleAsync(20);
        await SetupMarketAsync(cycle);
        // Lower id first so it sits at index 0; its floor weight (0.5) gives it a selectable slice despite a
        // near-zero capitalisation next to the giant.
        var tiny = await AddCompanyAsync(issuedShares: 1);
        var giant = await AddCompanyAsync(issuedShares: 1_000_000);
        await AddSnapshotAsync(tiny.Id, price: 1m, cycle);
        await AddSnapshotAsync(giant.Id, price: 10_000m, cycle);
        await AddAuditorAsync();

        // roll = 0.1 * totalWeight (1.5) = 0.15, inside the tiny company's 0.5 slice; stable-miss roll follows.
        await Service(enabled: true, new ScriptedRandom([0.1d, 0.9d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var rating = await context.CompanyRatings.AsNoTracking().SingleAsync();
        Assert.Equal(tiny.Id, rating.CompanyId);
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
        var industry = new Industry { Name = "Tech", SentimentValue = 500, SectorBeta = 0.5m };
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

    private async Task<Company> AddCompanyAsync(int issuedShares = 1000)
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

    private async Task<Auditor> AddAuditorAsync()
    {
        var auditor = new Auditor { Name = "Meridian Ratings", Description = "Test auditor", CreatedAt = DateTime.UtcNow };
        context.Auditors.Add(auditor);
        await context.SaveChangesAsync();
        return auditor;
    }

    private async Task<Participant> AddTraderAsync(Temperament temperament, RiskProfile riskProfile, decimal reserved)
    {
        var trader = new Participant
        {
            Name = "Trader",
            Type = ParticipantType.Individual,
            Temperament = temperament,
            RiskProfile = riskProfile,
            InitialBalance = 10_000m,
            CurrentBalance = 10_000m,
            ReservedBalance = reserved,
            IsActive = true,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
    }

    private async Task<Order> AddBuyOrderAsync(int participantId, int companyId, int quantity, decimal price, decimal reserved, MarketCycle cycle)
    {
        var now = DateTime.UtcNow;
        var order = new Order
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = quantity,
            FilledQuantity = 0,
            LimitPrice = price,
            ReservedCashAmount = reserved,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order;
    }

    private async Task<Order> AddSellOrderAsync(
        int participantId,
        int companyId,
        int quantity,
        int filledQuantity,
        decimal price,
        MarketCycle cycle)
    {
        var now = DateTime.UtcNow;
        var order = new Order
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Type = OrderType.Sell,
            Status = filledQuantity > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Open,
            Quantity = quantity,
            FilledQuantity = filledQuantity,
            LimitPrice = price,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order;
    }

    private async Task<Crisis> AddCrisisAsync(MarketCycle cycle)
    {
        var crisis = new Crisis
        {
            Title = "Shock",
            Content = "Body",
            Scope = CrisisScope.Global,
            TriggeredInCycleId = cycle.Id,
            TriggeredInCycleNumber = cycle.CycleNumber,
            DurationCycles = 20,
            TriggeredAt = DateTime.UtcNow,
        };
        context.Crises.Add(crisis);
        await context.SaveChangesAsync();
        return crisis;
    }

    private async Task AddRatingAsync(int companyId, int auditorId, CompanyRiskRating rating, MarketCycle cycle)
    {
        context.CompanyRatings.Add(new CompanyRating
        {
            CompanyId = companyId,
            AuditorId = auditorId,
            Rating = rating,
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
