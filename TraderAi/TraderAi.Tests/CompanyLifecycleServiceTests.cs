using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Drives the company life cycle with a scripted Random. Closure is deterministic and draws nothing; the appearance
// pass draws one NextDouble for the roll every cycle the market is below the cap — its chance is a population tier
// (10% under 50 companies, 5% under 100, 1% at or above) — then, only on a clearing roll, a share count, a price,
// an industry index, and one Next for the name. A delisting adds +0.25 to the same-cycle chance, so closure tests
// script a 0.99 roll that misses to isolate the delisting.
public sealed class CompanyLifecycleServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private int industryId;

    public CompanyLifecycleServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    private CompanyLifecycleService Service(
        bool enabled,
        Random random,
        CompanyFinancialService? financialService = null) =>
        new(
            context,
            Options.Create(new CompanyLifecycleOptions { Enabled = enabled }),
            Options.Create(new RandomChanceRatesOptions()),
            random,
            new MarketImpactService(context),
            financialService);

    private NewsService DeferredNews() =>
        new(
            context,
            new MarketCycleLock(),
            Options.Create(new NewsOptions()),
            Options.Create(new RandomChanceRatesOptions()),
            new MarketImpactService(context),
            new Random(1));

    [Fact]
    public async Task DisabledDoesNothing()
    {
        var cycle = await AddCycleAsync(200);
        await SetupMarketAsync(cycle, lastAppearanceCycleNumber: 1);
        var company = await AddCompanyAsync();

        await Service(enabled: false, new ScriptedRandom([], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Null(refreshed.ClosedInCycleId);
        Assert.Equal(1, await context.Companies.CountAsync());
    }

    [Fact]
    public async Task AppearanceListsCompanyWithFloatSnapshotAndNews()
    {
        // Empty market → the <50 tier at 10%; the 0.05 roll clears it. Then share count, price, industry index, name.
        var cycle = await AddCycleAsync(201);
        await SetupMarketAsync(cycle, lastAppearanceCycleNumber: 1);

        await Service(enabled: true, new ScriptedRandom([0.05d], [500, 100, 0, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var company = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(500, company.IssuedSharesCount);
        Assert.Equal(cycle.Id, company.CreatedInCycleId);
        Assert.Null(company.ClosedInCycleId);
        Assert.Equal(0m, company.CashBalance);
        Assert.Empty(context.CorporateCashTransactions);

        var floatOrder = await context.Orders.AsNoTracking().SingleAsync();
        Assert.Null(floatOrder.ParticipantId);
        Assert.Equal(OrderType.Sell, floatOrder.Type);
        Assert.Equal(OrderStatus.Open, floatOrder.Status);
        Assert.Equal(500, floatOrder.Quantity);
        Assert.Equal(100m, floatOrder.LimitPrice);

        // The issuer float lists at the listing reference, so it rests inside the executable band rather than the
        // waiting outer range.
        var bounds = OrderPriceBounds.FromReference(100m, 15m, 10m, 25m, 15m);
        Assert.True(bounds.IsWithinActiveBand(floatOrder.LimitPrice));

        var snapshot = await context.PriceSnapshots.AsNoTracking().SingleAsync();
        Assert.Equal(100m, snapshot.Price);
        Assert.Equal(50_000m, snapshot.Capitalization);

        Assert.Equal(1, await context.NewsPosts.CountAsync(post => post.Scope == NewsImpactScope.None));

        var refreshedMarket = await context.Markets.AsNoTracking().SingleAsync();
        Assert.Equal(201, refreshedMarket.LastCompanyAppearanceCycleNumber);
    }

    [Fact]
    public async Task AppearanceSeedsFinancialBaselineInTheListingCycle()
    {
        var cycle = await AddCycleForTradingDayAsync(cycleNumber: 201, dayNumber: 3);
        await SetupMarketAsync(cycle, lastAppearanceCycleNumber: 1);

        await Service(
                enabled: true,
                new ScriptedRandom([0.05d], [500, 100, 0, 0]),
                FinancialService(new Random(20260724)))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var company = await context.Companies.AsNoTracking().SingleAsync();
        var snapshot = await context.CompanyFinancialSnapshots.AsNoTracking().SingleAsync();
        Assert.Equal(company.Id, snapshot.CompanyId);
        Assert.Equal(cycle.Id, snapshot.CreatedInCycleId);
        Assert.Equal(3, snapshot.TradingDayNumber);
        Assert.Equal(CompanyFinancialSnapshotMoment.Seed, snapshot.Moment);
        Assert.Equal(CompanyFinancialMetric.All, snapshot.ChangedMetrics);
    }

    [Fact]
    public async Task AppearanceRollThatMissesListsNothingAndDrawsNoParameters()
    {
        var cycle = await AddCycleAsync(201);
        await SetupMarketAsync(cycle, lastAppearanceCycleNumber: 1);

        // Empty market → the 10% tier; the 0.5 roll misses it, so nothing is listed. The empty int queue proves no
        // parameter is drawn once the roll misses.
        await Service(enabled: true, new ScriptedRandom([0.5d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.Companies.CountAsync());
    }

    [Fact]
    public async Task HighTierBelowFiftyCompaniesListsOnModerateRoll()
    {
        // 10 live companies → the <50 tier at 10%; a 0.07 roll clears it (it would miss the 5% mid tier), so a
        // company lists. The plain companies carry no price history or ratings, so none qualifies to close.
        var cycle = await AddCycleAsync(10);
        await SetupMarketAsync(cycle, lastAppearanceCycleNumber: 1);
        await AddCompaniesAsync(10);

        await Service(enabled: true, new ScriptedRandom([0.07d], [500, 100, 0, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(11, await context.Companies.CountAsync());
    }

    [Fact]
    public async Task MidTierUnderHundredCompaniesMissesModerateRoll()
    {
        // 60 live companies → the 50–99 tier at 5%; a 0.07 roll misses it (it would clear the 10% high tier), so
        // nothing lists. The empty int queue proves no listing parameters are drawn.
        var cycle = await AddCycleAsync(10);
        await SetupMarketAsync(cycle, lastAppearanceCycleNumber: 1);
        await AddCompaniesAsync(60);

        await Service(enabled: true, new ScriptedRandom([0.07d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(60, await context.Companies.CountAsync());
    }

    [Fact]
    public async Task LowTierAtHundredOrMoreCompaniesMissesModerateRoll()
    {
        // 120 live companies → the ≥100 tier at 1%; a 0.03 roll misses it (it would clear the 5% mid tier), so
        // nothing lists.
        var cycle = await AddCycleAsync(10);
        await SetupMarketAsync(cycle, lastAppearanceCycleNumber: 1);
        await AddCompaniesAsync(120);

        await Service(enabled: true, new ScriptedRandom([0.03d], []))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(120, await context.Companies.CountAsync());
    }

    [Fact]
    public async Task ClosureBoostsAppearanceAndResetsCounter()
    {
        // A small-cap delists (the $1B backdrop keeps it under the 0.5% line), leaving the live count at 1 (the 10%
        // tier) and banking one closure boost (+0.25) → chance 0.35; the 0.1 roll clears it, so a replacement lists
        // the same cycle and the counter resets.
        var cycles = await AddCyclesAsync(21, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        await AddCapBackdropAsync(cycles[0]);
        var company = await AddCompanyAsync();
        await AddDecliningSnapshotsAsync(company.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);

        await Service(enabled: true, new ScriptedRandom([0.1d], [500, 100, 0, 0]))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        // Backdrop + the delisted target + the fresh listing.
        Assert.Equal(3, await context.Companies.CountAsync());
        Assert.Equal(1, await context.Companies.CountAsync(c => c.ClosedInCycleId == current.Id));
        Assert.Equal(2, await context.Companies.CountAsync(c => c.ClosedInCycleId == null));

        var refreshedMarket = await context.Markets.AsNoTracking().SingleAsync();
        Assert.Equal(0, refreshedMarket.CompanyClosuresSinceLastAppearance);
    }

    [Fact]
    public async Task AccumulatedClosuresRaiseAppearanceChance()
    {
        // 120 live companies sit in the 1% tier, but three banked closures add 0.75 → 76%. A 0.7 roll clears that,
        // where the 1% base alone would miss, and the counter resets. The plain companies never qualify to close.
        var cycle = await AddCycleAsync(50);
        await SetupMarketAsync(cycle, lastAppearanceCycleNumber: cycle.CycleNumber, closuresSinceLastAppearance: 3);
        await AddCompaniesAsync(120);

        await Service(enabled: true, new ScriptedRandom([0.7d], [500, 100, 0, 0]))
            .ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(121, await context.Companies.CountAsync());
        var refreshedMarket = await context.Markets.AsNoTracking().SingleAsync();
        Assert.Equal(0, refreshedMarket.CompanyClosuresSinceLastAppearance);
    }

    [Fact]
    public async Task SustainedPriceDeclineDelistsCompanyCancellingOrdersAndWipingHoldings()
    {
        // Cycles past the 100-cycle grace period so closure is active; a $1B backdrop keeps the target well under
        // the 0.5%-of-market protection line so it closes rather than being crashed.
        var cycles = await AddCyclesAsync(21, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        await AddCapBackdropAsync(cycles[0]);
        var company = await AddCompanyAsync();
        // 21 strictly decreasing closes → 20 down-moves, well past the 16-of-20 line.
        await AddDecliningSnapshotsAsync(company.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);

        var holder = await AddTraderAsync(balance: 5_000m, reserved: 0m);
        await AddHoldingAsync(holder.Id, company.Id, quantity: 10);
        var buyer = await AddTraderAsync(balance: 5_000m, reserved: 500m);
        var buy = await AddBuyOrderAsync(buyer.Id, company.Id, quantity: 5, price: 90m, reserved: 500m, current);
        await AddFloatSellOrderAsync(company.Id, quantity: 200, price: 100m, current);

        // The close boosts the appearance chance to 0.25; the 0.99 roll misses it, isolating the delisting.
        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == company.Id);
        Assert.Equal(current.Id, refreshed.ClosedInCycleId);
        Assert.NotNull(refreshed.ClosedAt);

        // Every order for the company is cancelled — the participant buy and the issuer float alike.
        Assert.Equal(0, await context.Orders.CountAsync(order =>
            order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled));

        // The buyer's reservation is released back and logged; no cash is credited to the holder.
        var refreshedBuyer = await context.Participants.AsNoTracking().SingleAsync(p => p.Id == buyer.Id);
        Assert.Equal(0m, refreshedBuyer.ReservedBalance);
        Assert.Equal(1, await context.MoneyTransactions.CountAsync(t =>
            t.Type == MoneyTransactionType.Release && t.ParticipantId == buyer.Id));
        var refreshedHolder = await context.Participants.AsNoTracking().SingleAsync(p => p.Id == holder.Id);
        Assert.Equal(5_000m, refreshedHolder.CurrentBalance);

        // Holdings are zeroed with no payout row.
        var holding = await context.Holdings.AsNoTracking().SingleAsync();
        Assert.Equal(0, holding.Quantity);
        Assert.Equal(0, await context.MoneyTransactions.CountAsync(t => t.Type == MoneyTransactionType.Credit));

        Assert.Equal(1, await context.NewsPosts.CountAsync(post => post.Scope == NewsImpactScope.None));
    }

    [Fact]
    public async Task ThreeConsecutiveHighRiskRatingsDelistCompany()
    {
        var cycles = await AddCyclesAsync(3, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync();
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, cycles[0]);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, cycles[1]);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, cycles[2]);

        // The close boosts the appearance chance to 0.25; the 0.99 roll misses it, isolating the delisting.
        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Equal(current.Id, refreshed.ClosedInCycleId);
    }

    [Theory]
    [InlineData(CompanyRiskRating.LowRisk)]
    [InlineData(CompanyRiskRating.Stable)]
    [InlineData(CompanyRiskRating.RaisedExpectations)]
    [InlineData(CompanyRiskRating.ExtraRaisedExpectations)]
    [InlineData(CompanyRiskRating.Extra)]
    public async Task AnyNonHighRiskRatingInTheStreakSparesCompany(CompanyRiskRating nonSevereRating)
    {
        var cycles = await AddCyclesAsync(3, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync();
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, cycles[0]);
        await AddRatingAsync(company.Id, nonSevereRating, cycles[1]);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, cycles[2]);

        // Nothing closes, but the one surviving company still sits in the 10% tier, so a roll is drawn; 0.99 misses.
        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Null(refreshed.ClosedInCycleId);
    }

    [Fact]
    public async Task HighFinancialClosureRiskAloneDoesNotQualifyCompany()
    {
        var current = await AddCycleAsync(200);
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(company.Id, price: 100m, current);
        await AddFinancialSnapshotAsync(
            company.Id,
            current,
            closureRiskScore: 100m,
            profitabilityScore: 0m,
            stabilityScore: 0m);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Null(refreshed.ClosedInCycleId);
    }

    [Fact]
    public async Task HigherFinancialClosureRiskBreaksEqualQualifiedCandidates()
    {
        var cycles = await AddCyclesAsync(21, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        await AddCapBackdropAsync(cycles[0]);
        var lowerRisk = await AddCompanyAsync();
        var higherRisk = await AddCompanyAsync();
        await AddDecliningSnapshotsAsync(lowerRisk.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);
        await AddDecliningSnapshotsAsync(higherRisk.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);
        await AddFinancialSnapshotAsync(
            lowerRisk.Id,
            current,
            closureRiskScore: 10m,
            profitabilityScore: 50m,
            stabilityScore: 50m);
        await AddFinancialSnapshotAsync(
            higherRisk.Id,
            cycles[0],
            closureRiskScore: 0m,
            profitabilityScore: 50m,
            stabilityScore: 50m);
        await AddFinancialSnapshotAsync(
            higherRisk.Id,
            current,
            closureRiskScore: 90m,
            profitabilityScore: 50m,
            stabilityScore: 50m,
            tradingDayNumber: 2,
            moment: CompanyFinancialSnapshotMoment.DayOpening);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Null((await context.Companies.AsNoTracking().SingleAsync(c => c.Id == lowerRisk.Id)).ClosedInCycleId);
        Assert.Equal(
            current.Id,
            (await context.Companies.AsNoTracking().SingleAsync(c => c.Id == higherRisk.Id)).ClosedInCycleId);
    }

    [Fact]
    public async Task LowerProfitabilityBreaksEqualFailureScores()
    {
        var cycles = await AddCyclesAsync(21, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        await AddCapBackdropAsync(cycles[0]);
        var profitable = await AddCompanyAsync();
        var unprofitable = await AddCompanyAsync();
        await AddDecliningSnapshotsAsync(profitable.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);
        await AddDecliningSnapshotsAsync(unprofitable.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);
        await AddFinancialSnapshotAsync(
            profitable.Id,
            current,
            closureRiskScore: 50m,
            profitabilityScore: 90m,
            stabilityScore: 50m);
        await AddFinancialSnapshotAsync(
            unprofitable.Id,
            current,
            closureRiskScore: 50m,
            profitabilityScore: 10m,
            stabilityScore: 50m);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Null((await context.Companies.AsNoTracking().SingleAsync(c => c.Id == profitable.Id)).ClosedInCycleId);
        Assert.Equal(
            current.Id,
            (await context.Companies.AsNoTracking().SingleAsync(c => c.Id == unprofitable.Id)).ClosedInCycleId);
    }

    [Fact]
    public async Task LowerStabilityBreaksEqualFailureScores()
    {
        var cycles = await AddCyclesAsync(21, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        await AddCapBackdropAsync(cycles[0]);
        var stable = await AddCompanyAsync();
        var unstable = await AddCompanyAsync();
        await AddDecliningSnapshotsAsync(stable.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);
        await AddDecliningSnapshotsAsync(unstable.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);
        await AddFinancialSnapshotAsync(
            stable.Id,
            current,
            closureRiskScore: 50m,
            profitabilityScore: 50m,
            stabilityScore: 90m);
        await AddFinancialSnapshotAsync(
            unstable.Id,
            current,
            closureRiskScore: 50m,
            profitabilityScore: 50m,
            stabilityScore: 10m);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Null((await context.Companies.AsNoTracking().SingleAsync(c => c.Id == stable.Id)).ClosedInCycleId);
        Assert.Equal(
            current.Id,
            (await context.Companies.AsNoTracking().SingleAsync(c => c.Id == unstable.Id)).ClosedInCycleId);
    }

    [Fact]
    public async Task OnlyTheWorstPerformerClosesWhenSeveralQualify()
    {
        var cycles = await AddCyclesAsync(21, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        await AddCapBackdropAsync(cycles[0]);
        var worse = await AddCompanyAsync();
        var milder = await AddCompanyAsync();
        // Both decline every cycle (each qualifies) and both are sub-0.5% next to the backdrop, but 'worse' falls
        // further, so only it is delisted.
        await AddDecliningSnapshotsAsync(worse.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);
        await AddDecliningSnapshotsAsync(milder.Id, cycles, startPrice: 100m, decrementPerCycle: 0.5m);

        // The close boosts the appearance chance to 0.25; the 0.99 roll misses it, isolating the delisting.
        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedWorse = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == worse.Id);
        var refreshedMilder = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == milder.Id);
        Assert.Equal(current.Id, refreshedWorse.ClosedInCycleId);
        Assert.Null(refreshedMilder.ClosedInCycleId);
    }

    [Fact]
    public async Task DealProtectedCompanyIsSkippedAndTheNextWorstClosesInstead()
    {
        var earlier = await AddCyclesAsync(20, firstNumber: 200);
        var current = await AddCycleForTradingDayAsync(cycleNumber: 220, dayNumber: 200);
        var cycles = new List<MarketCycle>(earlier) { current };
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        await AddCapBackdropAsync(cycles[0]);
        var protectedWorst = await AddCompanyAsync();
        var milder = await AddCompanyAsync();
        // Both decline every cycle so both qualify; 'protectedWorst' falls further, but its active deal protection
        // shields it, so the milder decliner is delisted instead.
        await AddDecliningSnapshotsAsync(protectedWorst.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);
        await AddDecliningSnapshotsAsync(milder.Id, cycles, startPrice: 100m, decrementPerCycle: 0.5m);
        protectedWorst.CloseProtectedUntilTradingDayNumber = 201;
        await context.SaveChangesAsync();

        // The close boosts the appearance chance to 0.25; the 0.99 roll misses it, isolating the delisting.
        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedProtected = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == protectedWorst.Id);
        var refreshedMilder = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == milder.Id);
        Assert.Null(refreshedProtected.ClosedInCycleId);
        Assert.Equal(current.Id, refreshedMilder.ClosedInCycleId);
    }

    [Fact]
    public async Task FullMarketWithNoQualifierForceClosesTheWorstPerformer()
    {
        var cycles = await AddCyclesAsync(2, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);

        // 300 live companies with no decline streak and no ratings — none qualifies on its own.
        var companies = await AddCompaniesAsync(300);
        // One dominant company ($1B) inflates total market cap so the doomed small-cap is far under the 0.5% line
        // and stays removable; it is flat, so it is never the worst performer itself.
        companies[0].IssuedSharesCount = 1_000_000;
        await context.SaveChangesAsync();
        await AddSnapshotAsync(companies[0].Id, price: 1_000m, cycles[0]);
        // Give one company a short two-point drop: too little history to be a decline-streak qualifier, but the
        // most-negative recent change, so the pressure valve delists exactly it.
        var doomed = companies[150];
        await AddSnapshotAsync(doomed.Id, price: 100m, cycles[0]);
        await AddSnapshotAsync(doomed.Id, price: 40m, current);

        // The force-delist boosts the appearance chance to 0.25; the 0.99 roll misses it, isolating the delisting.
        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.Companies.CountAsync(c => c.ClosedInCycleId != null));
        var refreshedDoomed = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == doomed.Id);
        Assert.Equal(current.Id, refreshedDoomed.ClosedInCycleId);
    }

    [Fact]
    public async Task DelistingDuringACrisisIsLoggedToTheTimeline()
    {
        var cycles = await AddCyclesAsync(21, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        await AddCapBackdropAsync(cycles[0]);
        var company = await AddCompanyAsync();
        await AddDecliningSnapshotsAsync(company.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);
        var crisis = await AddCrisisAsync(current);

        // The close boosts the appearance chance to 0.25; the 0.99 roll misses it, isolating the delisting.
        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow, crisis);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == company.Id);
        Assert.Equal(current.Id, refreshed.ClosedInCycleId);

        var timelineEvent = await context.CrisisEvents.AsNoTracking()
            .SingleAsync(row => row.Type == CrisisEventType.CompanyClosed);
        Assert.Equal(crisis.Id, timelineEvent.CrisisId);
        Assert.Equal(company.Id, timelineEvent.CompanyId);
    }

    [Fact]
    public async Task LargeCapDecliningCompanyIsCrashedNotClosed()
    {
        // The lone declining company is 100% of total market cap (far above the 0.5% line), so it is spared:
        // instead of a delisting it takes a 60% price cut (80 → 32) and stays live. The 0.99 appearance roll misses.
        var cycles = await AddCyclesAsync(21, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync(issuedShares: 1_000_000);
        await AddDecliningSnapshotsAsync(company.Id, cycles, startPrice: 100m, decrementPerCycle: 1m);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync();
        Assert.Null(refreshed.ClosedInCycleId);

        // The 60% cut stamped a fresh snapshot at 0.4 × the $80 close.
        var latest = await context.PriceSnapshots.AsNoTracking().OrderByDescending(s => s.Id).FirstAsync();
        Assert.Equal(32m, latest.Price);
        Assert.Equal(32_000_000m, latest.Capitalization);

        var news = await context.NewsPosts.AsNoTracking().SingleAsync(post => post.Scope == NewsImpactScope.Company);
        Assert.Equal(NewsImpactDirection.Decrease, news.Direction);
        Assert.Equal(60m, news.ImpactPercent);
        Assert.Equal(company.Id, news.TargetCompanyId);
        Assert.Equal(current.Id, news.ImpactAppliedInCycleId);

        var snapshotsBeforeApply = await context.PriceSnapshots.CountAsync(snapshot => snapshot.CompanyId == company.Id);
        Assert.Equal(0, await DeferredNews().ApplyPendingImpactsForCycleAsync(current, DateTime.UtcNow));
        await context.SaveChangesAsync();
        Assert.Equal(snapshotsBeforeApply, await context.PriceSnapshots.CountAsync(snapshot => snapshot.CompanyId == company.Id));
        Assert.Equal(32m, (await context.PriceSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync()).Price);

        // A crash is not a closure: it frees no slot and does not feed the appearance boost.
        var refreshedMarket = await context.Markets.AsNoTracking().SingleAsync();
        Assert.Equal(0, refreshedMarket.CompanyClosuresSinceLastAppearance);
    }

    [Fact]
    public async Task MarketDayFivePreventsLifecycleRepricingAndNews()
    {
        var current = await AddCycleForTradingDayAsync(cycleNumber: 200, dayNumber: 5);
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync(issuedShares: 1_000_000);
        await AddSnapshotAsync(company.Id, price: 100m, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        var snapshotCount = await context.PriceSnapshots.CountAsync();

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == company.Id);
        Assert.Null(refreshed.ClosedInCycleId);
        Assert.Equal(snapshotCount, await context.PriceSnapshots.CountAsync());
        Assert.Equal(0, await context.NewsPosts.CountAsync(post => post.Scope == NewsImpactScope.Company));
    }

    [Fact]
    public async Task MarketDaySixPermitsLifecycleRepricing()
    {
        var current = await AddCycleForTradingDayAsync(cycleNumber: 200, dayNumber: 6);
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync(issuedShares: 1_000_000);
        await AddSnapshotAsync(company.Id, price: 100m, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var latest = await context.PriceSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync();
        Assert.Equal(40m, latest.Price);
        Assert.Equal(1, await context.NewsPosts.CountAsync(post => post.Scope == NewsImpactScope.Company));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public async Task LateListedCompanyIsUntouchedDuringItsFirstFiveTradingDays(int currentDayNumber)
    {
        var listingCycle = await AddCycleForTradingDayAsync(cycleNumber: 200, dayNumber: 10);
        var current = currentDayNumber == 10
            ? listingCycle
            : await AddCycleForTradingDayAsync(cycleNumber: 200 + currentDayNumber - 10, dayNumber: currentDayNumber);
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync(issuedShares: 1_000_000, createdInCycleId: listingCycle.Id);
        await AddSnapshotAsync(company.Id, price: 100m, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        var snapshotCount = await context.PriceSnapshots.CountAsync();

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == company.Id);
        Assert.Null(refreshed.ClosedInCycleId);
        Assert.Equal(snapshotCount, await context.PriceSnapshots.CountAsync());
        Assert.Equal(0, await context.NewsPosts.CountAsync(post => post.Scope == NewsImpactScope.Company));
    }

    [Fact]
    public async Task TradingDaysNotCycleDistanceControlFreshness()
    {
        var listingCycle = await AddCycleForTradingDayAsync(cycleNumber: 200, dayNumber: 10);
        for (var tradingCycleNumber = 2; tradingCycleNumber <= 20; tradingCycleNumber++)
        {
            context.MarketCycles.Add(new MarketCycle
            {
                CycleNumber = 199 + tradingCycleNumber,
                TradingDayId = listingCycle.TradingDayId,
                TradingCycleNumber = tradingCycleNumber,
                Status = CycleStatus.Completed,
                StartedAt = DateTime.UtcNow,
            });
        }
        await context.SaveChangesAsync();

        var current = await AddCycleForTradingDayAsync(cycleNumber: 900, dayNumber: 14);
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync(issuedShares: 1_000_000, createdInCycleId: listingCycle.Id);
        await AddSnapshotAsync(company.Id, price: 100m, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        var snapshotCount = await context.PriceSnapshots.CountAsync();

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == company.Id);
        Assert.Null(refreshed.ClosedInCycleId);
        Assert.Equal(snapshotCount, await context.PriceSnapshots.CountAsync());
        Assert.Equal(0, await context.NewsPosts.CountAsync(post => post.Scope == NewsImpactScope.Company));
    }

    [Fact]
    public async Task LateListedCompanyBecomesEligibleOnItsSixthTradingDay()
    {
        var listingCycle = await AddCycleForTradingDayAsync(cycleNumber: 200, dayNumber: 10);
        var current = await AddCycleForTradingDayAsync(cycleNumber: 205, dayNumber: 15);
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var company = await AddCompanyAsync(issuedShares: 1_000_000, createdInCycleId: listingCycle.Id);
        await AddSnapshotAsync(company.Id, price: 100m, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var latest = await context.PriceSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.CompanyId == company.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstAsync();
        Assert.Equal(40m, latest.Price);
        Assert.Equal(1, await context.NewsPosts.CountAsync(post => post.Scope == NewsImpactScope.Company));
    }

    [Fact]
    public async Task FullMarketPressureSkipsFreshWorstPerformer()
    {
        var listingCycle = await AddCycleForTradingDayAsync(cycleNumber: 199, dayNumber: 16);
        var current = await AddCycleForTradingDayAsync(cycleNumber: 200, dayNumber: 20);
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        var companies = await AddCompaniesAsync(300);

        var dominant = companies[0];
        dominant.IssuedSharesCount = 1_000_000;
        var freshWorst = companies[150];
        freshWorst.CreatedInCycleId = listingCycle.Id;
        await context.SaveChangesAsync();

        await AddSnapshotAsync(dominant.Id, price: 1_000m, listingCycle);
        await AddSnapshotAsync(freshWorst.Id, price: 100m, listingCycle);
        await AddSnapshotAsync(freshWorst.Id, price: 20m, current);
        var matureNextWorst = companies[200];
        await AddSnapshotAsync(matureNextWorst.Id, price: 100m, listingCycle);
        await AddSnapshotAsync(matureNextWorst.Id, price: 40m, current);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshedFresh = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == freshWorst.Id);
        var refreshedMature = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == matureNextWorst.Id);
        Assert.Null(refreshedFresh.ClosedInCycleId);
        Assert.Equal(current.Id, refreshedMature.ClosedInCycleId);
    }

    [Fact]
    public async Task LegacyCompanyWithoutListingCycleRemainsEligible()
    {
        var current = await AddCycleForTradingDayAsync(cycleNumber: 200, dayNumber: 20);
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        await AddCapBackdropAsync(current);
        var company = await AddCompanyAsync(createdInCycleId: null);
        await AddSnapshotAsync(company.Id, price: 100m, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == company.Id);
        Assert.Equal(current.Id, refreshed.ClosedInCycleId);
    }

    [Fact]
    public async Task CompanyWithUnmappedListingCycleRemainsEligible()
    {
        var listingCycle = await AddCycleAsync(100);
        var current = await AddCycleForTradingDayAsync(cycleNumber: 200, dayNumber: 20);
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        await AddCapBackdropAsync(current);
        var company = await AddCompanyAsync(createdInCycleId: listingCycle.Id);
        await AddSnapshotAsync(company.Id, price: 100m, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);
        await AddRatingAsync(company.Id, CompanyRiskRating.HighRisk, current);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == company.Id);
        Assert.Equal(current.Id, refreshed.ClosedInCycleId);
    }

    [Fact]
    public async Task CompanyAboveHalfPercentOfMarketIsCrashedNotClosed()
    {
        // A declining company worth $10M is ~0.99% of a ~$1.01B market (≥ 0.5%), so it is crashed, not delisted —
        // proving the threshold is relative to total market cap, not a lone company being 100% of it.
        var cycles = await AddCyclesAsync(21, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);
        await AddCapBackdropAsync(cycles[0]);
        var company = await AddCompanyAsync(issuedShares: 100_000);
        // Declines 120 → 100 over 21 closes; latest cap = 100 × 100,000 = $10M.
        await AddDecliningSnapshotsAsync(company.Id, cycles, startPrice: 120m, decrementPerCycle: 1m);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var refreshed = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == company.Id);
        Assert.Null(refreshed.ClosedInCycleId);

        // The 60% cut stamped a fresh snapshot at 0.4 × the $100 close.
        var latest = await context.PriceSnapshots.AsNoTracking()
            .Where(s => s.CompanyId == company.Id)
            .OrderByDescending(s => s.Id).FirstAsync();
        Assert.Equal(40m, latest.Price);

        Assert.Equal(1, await context.NewsPosts.CountAsync(post => post.Scope == NewsImpactScope.Company));
    }

    [Fact]
    public async Task ForceDelistSkipsLargeCapAndRemovesTheWorstSmallerCompany()
    {
        var cycles = await AddCyclesAsync(2, firstNumber: 200);
        var current = cycles[^1];
        await SetupMarketAsync(current, lastAppearanceCycleNumber: current.CycleNumber);

        // 300 live companies, none with a decline streak or ratings — the pressure valve, not a qualifier, decides.
        var companies = await AddCompaniesAsync(300);

        // The most-negative performer is a large-cap dominating the market (40 × 2,000,000 = $80M, ~99.9% of total,
        // well above the 0.5% line): protected, so the valve skips it and instead removes the worst sub-threshold one.
        var bigCap = companies[100];
        bigCap.IssuedSharesCount = 2_000_000;
        await context.SaveChangesAsync();
        await AddSnapshotAsync(bigCap.Id, price: 100m, cycles[0]);
        await AddSnapshotAsync(bigCap.Id, price: 40m, current);

        var smaller = companies[200];
        await AddSnapshotAsync(smaller.Id, price: 100m, cycles[0]);
        await AddSnapshotAsync(smaller.Id, price: 60m, current);

        await Service(enabled: true, new ScriptedRandom([0.99d], []))
            .ProcessForCycleAsync(current.Id, current.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.Companies.CountAsync(c => c.ClosedInCycleId != null));
        var refreshedBig = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == bigCap.Id);
        var refreshedSmaller = await context.Companies.AsNoTracking().SingleAsync(c => c.Id == smaller.Id);
        Assert.Null(refreshedBig.ClosedInCycleId);
        Assert.Equal(current.Id, refreshedSmaller.ClosedInCycleId);
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

    private async Task<MarketCycle> AddCycleAsync(int number)
    {
        var cycle = new MarketCycle { CycleNumber = number, Status = CycleStatus.Running, StartedAt = DateTime.UtcNow };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();
        return cycle;
    }

    private async Task<List<MarketCycle>> AddCyclesAsync(int count, int firstNumber = 1)
    {
        var cycles = new List<MarketCycle>(count);
        for (var offset = 0; offset < count; offset++)
        {
            cycles.Add(await AddCycleAsync(firstNumber + offset));
        }

        return cycles;
    }

    private async Task<MarketCycle> AddCycleForTradingDayAsync(int cycleNumber, int dayNumber)
    {
        var day = new TradingDay
        {
            DayNumber = dayNumber,
            State = TradingSessionState.Trading,
            OpenedInCycleId = 0,
        };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();

        var cycle = new MarketCycle
        {
            CycleNumber = cycleNumber,
            TradingDayId = day.Id,
            TradingCycleNumber = 1,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();

        day.OpenedInCycleId = cycle.Id;
        await context.SaveChangesAsync();
        return cycle;
    }

    private async Task SetupMarketAsync(MarketCycle currentCycle, int lastAppearanceCycleNumber, int closuresSinceLastAppearance = 0)
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
            LastCompanyAppearanceCycleNumber = lastAppearanceCycleNumber,
            CompanyClosuresSinceLastAppearance = closuresSinceLastAppearance,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync();
    }

    private async Task<Company> AddCompanyAsync(int issuedShares = 1000, int? createdInCycleId = null)
    {
        var now = DateTime.UtcNow;
        var company = new Company
        {
            Name = $"Acme {Guid.NewGuid():N}",
            IndustryId = industryId,
            IssuedSharesCount = issuedShares,
            CreatedInCycleId = createdInCycleId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        return company;
    }

    // A single dominant, flat-priced company ($1B cap) so a failing target under test is a tiny slice of total
    // market cap — well under the 0.5% protection line — and closes rather than being spared.
    private async Task<Company> AddCapBackdropAsync(MarketCycle cycle)
    {
        var company = await AddCompanyAsync(issuedShares: 1_000_000);
        await AddSnapshotAsync(company.Id, price: 1_000m, cycle);
        return company;
    }

    private async Task<List<Company>> AddCompaniesAsync(int count)
    {
        var now = DateTime.UtcNow;
        var companies = new List<Company>(count);
        for (var index = 0; index < count; index++)
        {
            var company = new Company
            {
                Name = $"Acme {Guid.NewGuid():N}",
                IndustryId = industryId,
                IssuedSharesCount = 1000,
                CreatedAt = now,
                UpdatedAt = now,
            };
            companies.Add(company);
            context.Companies.Add(company);
        }

        await context.SaveChangesAsync();
        return companies;
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

    private async Task AddDecliningSnapshotsAsync(int companyId, List<MarketCycle> cycles, decimal startPrice, decimal decrementPerCycle)
    {
        for (var index = 0; index < cycles.Count; index++)
        {
            await AddSnapshotAsync(companyId, startPrice - (decrementPerCycle * index), cycles[index]);
        }
    }

    private async Task AddFinancialSnapshotAsync(
        int companyId,
        MarketCycle cycle,
        decimal closureRiskScore,
        decimal profitabilityScore,
        decimal stabilityScore,
        int tradingDayNumber = 1,
        CompanyFinancialSnapshotMoment moment = CompanyFinancialSnapshotMoment.Seed)
    {
        context.CompanyFinancialSnapshots.Add(new CompanyFinancialSnapshot
        {
            CompanyId = companyId,
            CreatedInCycleId = cycle.Id,
            TradingDayNumber = tradingDayNumber,
            Moment = moment,
            CreatedAt = DateTime.UtcNow,
            Revenue = 100m,
            NetProfit = 10m,
            OperatingCashFlow = 10m,
            TotalAssets = 100m,
            TotalLiabilities = 50m,
            TotalDebt = 25m,
            ManagementRevenueForecast = 100m,
            ManagementProfitForecast = 10m,
            ManagementOperatingCashFlowForecast = 10m,
            ManagementConfidenceScore = 50m,
            ProfitabilityScore = profitabilityScore,
            StabilityScore = stabilityScore,
            ClosureRiskScore = closureRiskScore,
        });
        await context.SaveChangesAsync();
    }

    private async Task<Participant> AddTraderAsync(decimal balance, decimal reserved)
    {
        var trader = new Participant
        {
            Name = $"Trader {Guid.NewGuid():N}",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = balance,
            CurrentBalance = balance,
            ReservedBalance = reserved,
            IsActive = true,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
    }

    private async Task AddHoldingAsync(int participantId, int companyId, int quantity)
    {
        context.Holdings.Add(new Holding
        {
            ParticipantId = participantId,
            CompanyId = companyId,
            Quantity = quantity,
            AverageCost = 100m,
        });
        await context.SaveChangesAsync();
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

    private async Task AddFloatSellOrderAsync(int companyId, int quantity, decimal price, MarketCycle cycle)
    {
        var now = DateTime.UtcNow;
        context.Orders.Add(new Order
        {
            ParticipantId = null,
            CompanyId = companyId,
            Type = OrderType.Sell,
            Status = OrderStatus.Open,
            Quantity = quantity,
            FilledQuantity = 0,
            LimitPrice = price,
            ReservedCashAmount = 0m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync();
    }

    private async Task AddRatingAsync(int companyId, CompanyRiskRating rating, MarketCycle cycle)
    {
        var auditor = new Auditor { Name = "Ratings", Description = "Test", CreatedAt = DateTime.UtcNow };
        context.Auditors.Add(auditor);
        await context.SaveChangesAsync();

        context.CompanyRatings.Add(new CompanyRating
        {
            CompanyId = companyId,
            AuditorId = auditor.Id,
            Rating = rating,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private CompanyFinancialService FinancialService(Random random)
    {
        var options = new CompanyFinancialOptions();
        return new CompanyFinancialService(
            context,
            Options.Create(options),
            Options.Create(new RandomChanceRatesOptions()),
            Options.Create(new TradingClockOptions()),
            random,
            new CompanyFinancialScorer(Options.Create(options)));
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
