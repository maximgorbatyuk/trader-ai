using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the auditor with a scripted Random so company selection, the hidden-issue roll, the drop size, and the
// per-buyer cancel rolls are all forced. Backfilling auditors draws nothing; each acting auditor then draws one
// NextDouble to pick a company, one to roll for an issue, one more (Extra only) for the drop, and one per
// eligible buy order, plus one Next per news headline.
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

    private AuditorService Service(bool enabled, Random random) =>
        new(context, Options.Create(new AuditorOptions { Enabled = enabled }), random, new MarketImpactService(context));

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

        // pick, issue roll hits (< 0.10), drop size 0.5 → 25%, cancel roll hits (< 0.70); one int for the headline.
        await Service(enabled: true, new ScriptedRandom([0.9d, 0.05d, 0.5d, 0.1d], [0]))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var rating = await context.CompanyRatings.AsNoTracking().SingleAsync();
        Assert.Equal(CompanyRiskRating.Extra, rating.Rating);
        Assert.Equal(25m, rating.ImpactPercent);

        // A fresh snapshot 25% below the last price of 200.
        var latest = await context.PriceSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync();
        Assert.Equal(150m, latest.Price);

        var news = await context.NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal(NewsImpactScope.Company, news.Scope);
        Assert.Equal(NewsImpactDirection.Decrease, news.Direction);
        Assert.Equal(25m, news.ImpactPercent);
        Assert.Equal(company.Id, news.TargetCompanyId);

        var refreshedBuy = await context.Orders.AsNoTracking().FirstAsync(order => order.Id == buy.Id);
        Assert.Equal(OrderStatus.Cancelled, refreshedBuy.Status);
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
