using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class CompanyFinancialServiceTests : IDisposable
{
    private const int TradingCyclesPerDay = 210;
    private const int MiddayTradingCycleNumber = TradingCyclesPerDay / 2 + 1;

    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public CompanyFinancialServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task SeedScalesFinancialValuesFromListingCapitalization()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 1);
        var company = await AddCompanyAsync(issuedShares: 1_000);
        var random = new ScriptedRandom(Enumerable.Repeat(0.5d, 12));

        var snapshot = await Service(random).StageSeedSnapshotAsync(
            company,
            listingPrice: 100m,
            cycle.Id,
            tradingDayNumber: 1,
            now: new DateTime(2026, 7, 23, 8, 0, 0, DateTimeKind.Utc));

        Assert.NotNull(snapshot);
        Assert.Equal(100_000m, snapshot.TotalAssets);
        Assert.Equal(60_000m, snapshot.Revenue);
        Assert.Equal(6_000m, snapshot.NetProfit);
        Assert.Equal(6_000m, snapshot.OperatingCashFlow);
        Assert.Equal(45_000m, snapshot.TotalLiabilities);
        Assert.Equal(18_000m, snapshot.TotalDebt);
        Assert.Equal(2.25m, snapshot.ExpectedDividendPerShare);
        Assert.Equal(2_250m, snapshot.ExpectedDividendPool);
        Assert.Equal(EntityState.Added, context.Entry(snapshot).State);
        Assert.Empty(await context.CompanyFinancialSnapshots.AsNoTracking().ToListAsync());
        Assert.Equal(0, random.Remaining);
    }

    [Fact]
    public async Task SeedMaintainsFinancialInvariantsAndStartsWithPositiveEquity()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 1);
        var company = await AddCompanyAsync(issuedShares: 2_000);

        var snapshot = await Service(new Random(20260723)).StageSeedSnapshotAsync(
            company,
            listingPrice: 73.25m,
            cycle.Id,
            tradingDayNumber: 1,
            now: DateTime.UtcNow);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Revenue >= 0m);
        Assert.True(snapshot.TotalAssets > 0m);
        Assert.InRange(snapshot.TotalLiabilities, 0m, snapshot.TotalAssets);
        Assert.InRange(snapshot.TotalDebt, 0m, snapshot.TotalLiabilities);
        Assert.True(snapshot.ExpectedDividendPerShare >= 0m);
        Assert.Equal(
            Math.Round(
                snapshot.ExpectedDividendPerShare * company.IssuedSharesCount,
                2,
                MidpointRounding.AwayFromZero),
            snapshot.ExpectedDividendPool);
        Assert.True(snapshot.DividendCoverageRatio >= 0m);
        Assert.InRange(snapshot.BusinessRiskScore, 0m, 100m);
        Assert.InRange(snapshot.ManagementConfidenceScore, 0m, 100m);
    }

    [Fact]
    public async Task FixedSeedProducesTheSameFinancialState()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 1);
        var firstCompany = await AddCompanyAsync();
        var secondCompany = await AddCompanyAsync();

        var first = await Service(new Random(42)).StageSeedSnapshotAsync(
            firstCompany,
            50m,
            cycle.Id,
            1,
            DateTime.UtcNow);
        var second = await Service(new Random(42)).StageSeedSnapshotAsync(
            secondCompany,
            50m,
            cycle.Id,
            1,
            DateTime.UtcNow);

        Assert.Equal(StateOf(first!), StateOf(second!));
    }

    [Fact]
    public async Task DistinctSeedsProduceDifferentFinancialStates()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 1);
        var firstCompany = await AddCompanyAsync();
        var secondCompany = await AddCompanyAsync();

        var first = await Service(new Random(1)).StageSeedSnapshotAsync(
            firstCompany,
            50m,
            cycle.Id,
            1,
            DateTime.UtcNow);
        var second = await Service(new Random(2)).StageSeedSnapshotAsync(
            secondCompany,
            50m,
            cycle.Id,
            1,
            DateTime.UtcNow);

        Assert.NotEqual(StateOf(first!), StateOf(second!));
    }

    [Fact]
    public async Task SeedForecastsStayWithinConfiguredDeviationFromActualValues()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        var financialOptions = new CompanyFinancialOptions
        {
            MaximumForecastDeviationRatio = 0.03m,
        };

        var snapshot = await Service(
                new ScriptedRandom(
                [
                    0.5d, 0.5d, 0.5d, 0.5d, 0.5d, 0.5d,
                    0.5d, 0.5d, 0d, 1d, 0d, 0.5d,
                ]),
                financialOptions: financialOptions)
            .StageSeedSnapshotAsync(
                company,
                80m,
                cycle.Id,
                1,
                DateTime.UtcNow);

        Assert.NotNull(snapshot);
        AssertWithinDeviation(snapshot.Revenue, snapshot.ManagementRevenueForecast, 0.03m);
        AssertWithinDeviation(snapshot.NetProfit, snapshot.ManagementProfitForecast, 0.03m);
        AssertWithinDeviation(
            snapshot.OperatingCashFlow,
            snapshot.ManagementOperatingCashFlowForecast,
            0.03m);
    }

    [Fact]
    public async Task SeedPopulatesAggregateScoresAndMarksAllMetrics()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();

        var snapshot = await Service(new Random(7)).StageSeedSnapshotAsync(
            company,
            100m,
            cycle.Id,
            1,
            DateTime.UtcNow);

        Assert.NotNull(snapshot);
        Assert.InRange(snapshot.ProfitabilityScore, 0.000001m, 100m);
        Assert.InRange(snapshot.StabilityScore, 0.000001m, 100m);
        Assert.InRange(snapshot.ClosureRiskScore, 0.000001m, 100m);
        Assert.True(Enum.IsDefined(snapshot.ProfitabilityLevel));
        Assert.True(Enum.IsDefined(snapshot.FinancialVolatilityLevel));
        Assert.True(Enum.IsDefined(snapshot.ClosureRiskLevel));
        Assert.True(Enum.IsDefined(snapshot.ManagementOutlook));
        Assert.Equal(CompanyFinancialSnapshotMoment.Seed, snapshot.Moment);
        Assert.Equal(CompanyFinancialMetric.All, snapshot.ChangedMetrics);
    }

    [Fact]
    public async Task SeedDividendIsCappedByProfitPayoutAndCashFlowCoverage()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        var financialOptions = new CompanyFinancialOptions
        {
            MaximumExpectedDividendPayoutRatio = 0.10m,
            MinimumExpectedDividendCoverageRatio = 2m,
        };
        var snapshot = await Service(
                new ScriptedRandom(
                [
                    0.5d, 0.5d, 0d, 0d, 0.5d, 0.5d,
                    1d, 0.5d, 0.5d, 0.5d, 0.5d, 0.5d,
                ]),
                financialOptions: financialOptions)
            .StageSeedSnapshotAsync(
                company,
                100m,
                cycle.Id,
                1,
                DateTime.UtcNow);

        Assert.NotNull(snapshot);
        Assert.True(
            snapshot.ExpectedDividendPool
            <= Math.Max(0m, snapshot.NetProfit)
                * financialOptions.MaximumExpectedDividendPayoutRatio);
        Assert.True(
            snapshot.ExpectedDividendPool
            <= Math.Max(0m, snapshot.OperatingCashFlow)
                / financialOptions.MinimumExpectedDividendCoverageRatio);
        Assert.Equal(
            Math.Round(
                snapshot.ExpectedDividendPerShare * company.IssuedSharesCount,
                2,
                MidpointRounding.AwayFromZero),
            snapshot.ExpectedDividendPool);
        Assert.True(
            snapshot.DividendCoverageRatio
            >= financialOptions.MinimumExpectedDividendCoverageRatio);
    }

    [Fact]
    public async Task SeedWithNegativeProfitAndCashFlowExpectsNoDividend()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        var chanceRates = new RandomChanceRatesOptions();
        chanceRates.RandomMagnitudeBands.FinancialSeedNetMarginMin = -0.10m;
        chanceRates.RandomMagnitudeBands.FinancialSeedNetMarginMax = 0.10m;

        var snapshot = await Service(
                new ScriptedRandom(
                [
                    0.5d, 0.5d, 0d, 0.5d, 0.5d, 0.5d,
                    1d, 0.5d, 0.5d, 0.5d, 0.5d, 0.5d,
                ]),
                chanceRates: chanceRates)
            .StageSeedSnapshotAsync(
                company,
                100m,
                cycle.Id,
                1,
                DateTime.UtcNow);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.NetProfit < 0m);
        Assert.True(snapshot.OperatingCashFlow < 0m);
        Assert.Equal(0m, snapshot.ExpectedDividendPerShare);
        Assert.Equal(0m, snapshot.ExpectedDividendPool);
        Assert.Equal(0m, snapshot.DividendCoverageRatio);
    }

    [Fact]
    public async Task SeedBusinessRiskIsAdjustedByIndustryTrend()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 1);
        var risingCompany = await AddCompanyAsync(sentimentValue: 100);
        var fallingCompany = await AddCompanyAsync(sentimentValue: -100);

        var rising = await Service(new Random(123)).StageSeedSnapshotAsync(
            risingCompany,
            100m,
            cycle.Id,
            1,
            DateTime.UtcNow);
        var falling = await Service(new Random(123)).StageSeedSnapshotAsync(
            fallingCompany,
            100m,
            cycle.Id,
            1,
            DateTime.UtcNow);

        Assert.NotNull(rising);
        Assert.NotNull(falling);
        Assert.InRange(rising.BusinessRiskScore, 0m, 100m);
        Assert.InRange(falling.BusinessRiskScore, 0m, 100m);
        Assert.True(rising.BusinessRiskScore < falling.BusinessRiskScore);
    }

    [Fact]
    public async Task DisabledProcessingDrawsNothingAndStagesNothing()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(company, cycle, dayNumber: 1);

        await Service(new ScriptedRandom([]), enabled: false)
            .ProcessForCycleAsync(cycle.Id, DateTime.UtcNow);

        Assert.Equal(
            1,
            await context.CompanyFinancialSnapshots.AsNoTracking().CountAsync());
        Assert.DoesNotContain(
            context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
            entry => entry.State == EntityState.Added);
    }

    [Fact]
    public async Task NonCheckpointCycleDrawsNothingAndStagesNothing()
    {
        var cycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 2);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(company, cycle, dayNumber: 1);

        await Service(new ScriptedRandom([]))
            .ProcessForCycleAsync(cycle.Id, DateTime.UtcNow);

        Assert.Equal(
            1,
            await context.CompanyFinancialSnapshots.AsNoTracking().CountAsync());
        Assert.DoesNotContain(
            context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
            entry => entry.State == EntityState.Added);
    }

    [Fact]
    public async Task OpeningAndMiddayStageOneSnapshotPerLiveCompanyAndSkipClosedCompanies()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var middayCycle = await AddCycleAsync(day, tradingCycleNumber: MiddayTradingCycleNumber);
        var first = await AddCompanyAsync();
        var second = await AddCompanyAsync();
        var closed = await AddCompanyAsync(closedInCycleId: seedCycle.Id);
        await AddSnapshotAsync(first, seedCycle, dayNumber: 1);
        await AddSnapshotAsync(second, seedCycle, dayNumber: 1);
        await AddSnapshotAsync(closed, seedCycle, dayNumber: 1);
        var random = new ScriptedRandom(Enumerable.Repeat(0.99d, 52));

        await Service(random).ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();
        await Service(random).ProcessForCycleAsync(middayCycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var rows = await context.CompanyFinancialSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.Moment != CompanyFinancialSnapshotMoment.Seed)
            .ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Count(row =>
            row.Moment == CompanyFinancialSnapshotMoment.DayOpening));
        Assert.Equal(2, rows.Count(row =>
            row.Moment == CompanyFinancialSnapshotMoment.Midday));
        Assert.DoesNotContain(rows, row => row.CompanyId == closed.Id);
        Assert.Equal(0, random.Remaining);
    }

    [Fact]
    public async Task RepeatedCheckpointCallIsIdempotentBeforeSaveAndDrawsNothingAgain()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(company, seedCycle, dayNumber: 1);
        var random = new ScriptedRandom(Enumerable.Repeat(0.99d, 13));
        var service = Service(random);

        await service.ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);
        await service.ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        Assert.Single(
            context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
            entry => entry.State == EntityState.Added);
        Assert.Equal(0, random.Remaining);
    }

    [Fact]
    public async Task NoSelectedChangesStillStageAnUnchangedSnapshot()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        var seed = await AddSnapshotAsync(company, seedCycle, dayNumber: 1);

        await Service(new ScriptedRandom(
                new[] { 0.5d }.Concat(Enumerable.Repeat(0.99d, 12))))
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.Equal(CompanyFinancialMetric.None, staged.ChangedMetrics);
        Assert.Equal(StateOf(seed), StateOf(staged));
        Assert.Equal(CompanyFinancialSnapshotMoment.DayOpening, staged.Moment);
    }

    [Fact]
    public async Task SelectedMetricIsFlaggedWhenRoundingKeepsItsStoredValue()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        var seed = await AddSnapshotAsync(company, seedCycle, dayNumber: 1);
        var chanceRates = new RandomChanceRatesOptions();
        chanceRates.RandomMagnitudeBands.FinancialDividendUpdateMin = 0m;
        chanceRates.RandomMagnitudeBands.FinancialDividendUpdateMax = 0m;
        var draws = new List<double> { 0.5d };
        draws.AddRange(Enumerable.Repeat(0.99d, 6));
        draws.AddRange([0d, 0d, 0d]);
        draws.AddRange(Enumerable.Repeat(0.99d, 5));

        await Service(new ScriptedRandom(draws), chanceRates: chanceRates)
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.Equal(seed.ExpectedDividendPerShare, staged.ExpectedDividendPerShare);
        Assert.Equal(
            CompanyFinancialMetric.ExpectedDividendPerShare,
            staged.ChangedMetrics);
    }

    [Fact]
    public async Task UnselectedInvariantCorrectionIsNotMarkedAsSelected()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        var seed = await AddSnapshotAsync(
            company,
            seedCycle,
            dayNumber: 1,
            configure: snapshot =>
            {
                snapshot.ExpectedDividendPerShare = 0.10m;
                snapshot.ExpectedDividendPool = 100m;
                snapshot.DividendCoverageRatio = 0.12m;
            });

        await Service(new ScriptedRandom(
                new[] { 0.5d }.Concat(Enumerable.Repeat(0.99d, 12))))
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.True(staged.ExpectedDividendPerShare < seed.ExpectedDividendPerShare);
        Assert.Equal(CompanyFinancialMetric.None, staged.ChangedMetrics);
    }

    [Fact]
    public async Task SelectedMetricsChangeInTheDocumentedFixedOrder()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        var seed = await AddSnapshotAsync(company, seedCycle, dayNumber: 1);
        var random = new ScriptedRandom(
        [
            0.5d,
            0d, 0d, 0d,
            0.99d,
            0.99d,
            0.99d,
            0.99d,
            0.99d,
            0.99d,
            0d, 0d, 0d,
            0.99d,
            0.99d,
            0.99d,
            0.99d,
        ]);

        await Service(random).ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.Equal(
            CompanyFinancialMetric.Revenue | CompanyFinancialMetric.BusinessRisk,
            staged.ChangedMetrics);
        Assert.True(staged.Revenue > seed.Revenue);
        Assert.True(staged.BusinessRiskScore < seed.BusinessRiskScore);
        Assert.Equal(seed.NetProfit, staged.NetProfit);
        Assert.Equal(seed.ManagementConfidenceScore, staged.ManagementConfidenceScore);
        Assert.Equal(0, random.Remaining);
    }

    [Theory]
    [InlineData(100, 0.50d, true)]
    [InlineData(-100, 0.50d, false)]
    [InlineData(100, 0.90d, false)]
    [InlineData(-100, 0.10d, true)]
    public async Task IndustryBiasInfluencesButDoesNotDictateDirection(
        int sentimentValue,
        double directionRoll,
        bool expectedIncrease)
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync(sentimentValue: sentimentValue);
        var seed = await AddSnapshotAsync(company, seedCycle, dayNumber: 1);
        var draws = new List<double> { 0.5d, 0d, directionRoll, 0d };
        draws.AddRange(Enumerable.Repeat(0.99d, 11));

        await Service(new ScriptedRandom(draws))
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.Equal(
            expectedIncrease,
            staged.Revenue > seed.Revenue);
    }

    [Fact]
    public async Task RepeatedDeteriorationCanReachNegativeEquityOnlyGradually()
    {
        var company = await AddCompanyAsync();
        var seedCycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 2);
        await AddSnapshotAsync(
            company,
            seedCycle,
            dayNumber: 1,
            configure: snapshot =>
            {
                snapshot.TotalAssets = 100m;
                snapshot.TotalLiabilities = 65m;
                snapshot.TotalDebt = 30m;
            });
        var draws = new List<double>();
        for (var index = 0; index < 12; index++)
        {
            draws.AddRange(
            [
                0.5d,
                0.99d,
                0.99d,
                0.99d,
                0d, 0.99d, 1d,
                0d, 0.99d, 1d,
                0.99d,
                0.99d,
                0.99d,
                0.99d,
                0.99d,
                0.99d,
                0.99d,
            ]);
        }
        var random = new ScriptedRandom(draws);
        var service = Service(random);

        for (var dayNumber = 2; dayNumber <= 13; dayNumber++)
        {
            var cycle = await AddCycleAsync(dayNumber, tradingCycleNumber: 1);
            await service.ProcessForCycleAsync(cycle.Id, DateTime.UtcNow);
            await context.SaveChangesAsync();
        }

        var history = await context.CompanyFinancialSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.CompanyId == company.Id
                && snapshot.Moment == CompanyFinancialSnapshotMoment.DayOpening)
            .OrderBy(snapshot => snapshot.TradingDayNumber)
            .ToListAsync();
        Assert.Equal(12, history.Count);
        Assert.All(history.Take(10), snapshot =>
            Assert.True(snapshot.TotalAssets >= snapshot.TotalLiabilities));
        Assert.Contains(history.Skip(10), snapshot =>
            snapshot.TotalLiabilities > snapshot.TotalAssets);
        Assert.All(history.Zip(history.Skip(1)), pair =>
        {
            Assert.InRange(
                pair.Second.TotalAssets / pair.First.TotalAssets,
                0.9798m,
                0.9802m);
            Assert.InRange(
                pair.Second.TotalLiabilities / pair.First.TotalLiabilities,
                1.0198m,
                1.0202m);
        });
        Assert.Equal(0, random.Remaining);
    }

    [Fact]
    public async Task DebtIsClampedToLiabilities()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(
            company,
            seedCycle,
            dayNumber: 1,
            configure: snapshot =>
            {
                snapshot.TotalAssets = 150m;
                snapshot.TotalLiabilities = 100m;
                snapshot.TotalDebt = 99m;
            });
        var draws = new List<double>
        {
            0.5d,
            0.99d,
            0.99d,
            0.99d,
            0.99d,
            0d, 0d, 1d,
            0d, 0.99d, 1d,
        };
        draws.AddRange(Enumerable.Repeat(0.99d, 6));

        await Service(new ScriptedRandom(draws))
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.Equal(staged.TotalLiabilities, staged.TotalDebt);
    }

    [Fact]
    public async Task UpdatesDoNotTouchCorporateCashOrItsLedger()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync(cashBalance: 12_345.67m);
        await AddSnapshotAsync(company, seedCycle, dayNumber: 1);
        context.CorporateCashTransactions.Add(new CorporateCashTransaction
        {
            CompanyId = company.Id,
            Type = CorporateCashTransactionType.OperatingIncome,
            Amount = 100m,
            CreatedInCycleId = seedCycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        var draws = new[] { 0.5d }
            .Concat(Enumerable.Repeat(new[] { 0d, 0d, 0d }, 12)
                .SelectMany(row => row));

        await Service(new ScriptedRandom(draws))
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.Equal(
            12_345.67m,
            await context.Companies
                .Where(candidate => candidate.Id == company.Id)
                .Select(candidate => candidate.CashBalance)
                .SingleAsync());
        var ledger = await context.CorporateCashTransactions.AsNoTracking().ToListAsync();
        Assert.Single(ledger);
        Assert.Equal(100m, ledger[0].Amount);
    }

    [Fact]
    public async Task CheckpointDrawsOneCompanyImpulseBeforeMetricRolls()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(company, seedCycle, dayNumber: 1);
        var random = new ScriptedRandom(
            new[] { 0.25d }.Concat(Enumerable.Repeat(0.99d, 12)));

        await Service(random).ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        Assert.Equal(0, random.Remaining);
    }

    [Fact]
    public async Task IndependentMetricDirectionCanOvercomeTheCompanyImpulse()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync(sentimentValue: 100);
        var seed = await AddSnapshotAsync(company, seedCycle, dayNumber: 1);
        var draws = new List<double>
        {
            0d,
            0d, 0.55d, 0d,
            0d, 0.99d, 0d,
        };
        draws.AddRange(Enumerable.Repeat(0.99d, 10));

        await Service(new ScriptedRandom(draws))
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.True(staged.Revenue > seed.Revenue);
        Assert.True(staged.NetProfit < seed.NetProfit);
    }

    [Theory]
    [InlineData(100, 0.90d, 0.99d, false)]
    [InlineData(-100, 0.10d, 0d, true)]
    public async Task IndependentDirectionDominatesEvenAtMaximumIndustryImpulseWeight(
        int sentimentValue,
        double impulseRoll,
        double directionRoll,
        bool expectedIncrease)
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync(sentimentValue: sentimentValue);
        var seed = await AddSnapshotAsync(company, seedCycle, dayNumber: 1);
        var financialOptions = new CompanyFinancialOptions
        {
            IndustryImpulseWeight = 1m,
        };
        var draws = new List<double>
        {
            impulseRoll,
            0d, directionRoll, 0d,
        };
        draws.AddRange(Enumerable.Repeat(0.99d, 11));

        await Service(
                new ScriptedRandom(draws),
                financialOptions: financialOptions)
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.Equal(expectedIncrease, staged.Revenue > seed.Revenue);
    }

    [Fact]
    public async Task SharedCompanyImpulseCorrelatesAlignedMetricDirections()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        var seed = await AddSnapshotAsync(company, seedCycle, dayNumber: 1);
        var draws = new List<double>
        {
            0d,
            0d, 0.60d, 0d,
            0d, 0.60d, 0d,
        };
        draws.AddRange(Enumerable.Repeat(0.99d, 10));

        await Service(new ScriptedRandom(draws))
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.True(staged.Revenue > seed.Revenue);
        Assert.True(staged.NetProfit > seed.NetProfit);
    }

    [Fact]
    public async Task ProfitAndCashFlowDeteriorationReducesDividendEvenWhenDividendWasNotSelected()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        var seed = await AddSnapshotAsync(
            company,
            seedCycle,
            dayNumber: 1,
            configure: snapshot =>
            {
                snapshot.NetProfit = 200m;
                snapshot.OperatingCashFlow = 200m;
                snapshot.ExpectedDividendPerShare = 0.10m;
                snapshot.ExpectedDividendPool = 100m;
                snapshot.DividendCoverageRatio = 2m;
            });
        var chanceRates = new RandomChanceRatesOptions();
        chanceRates.RandomMagnitudeBands.FinancialOperatingUpdateMin = 0.75m;
        chanceRates.RandomMagnitudeBands.FinancialOperatingUpdateMax = 0.75m;
        var draws = new List<double>
        {
            0.5d,
            0.99d,
            0d, 0.99d, 1d,
            0d, 0.99d, 1d,
        };
        draws.AddRange(Enumerable.Repeat(0.99d, 9));

        await Service(new ScriptedRandom(draws), chanceRates: chanceRates)
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.True(staged.ExpectedDividendPool < seed.ExpectedDividendPool);
        Assert.True(staged.ExpectedDividendPerShare < seed.ExpectedDividendPerShare);
        Assert.True(
            staged.ExpectedDividendPool
            <= Math.Max(0m, staged.NetProfit) * 0.60m);
        Assert.True(
            staged.ExpectedDividendPool
            <= Math.Max(0m, staged.OperatingCashFlow) / 1.20m);
        Assert.True(staged.DividendCoverageRatio >= 1.20m);
        Assert.True(staged.ChangedMetrics.HasFlag(CompanyFinancialMetric.NetProfit));
        Assert.True(staged.ChangedMetrics.HasFlag(CompanyFinancialMetric.OperatingCashFlow));
        Assert.False(staged.ChangedMetrics.HasFlag(
            CompanyFinancialMetric.ExpectedDividendPerShare));
    }

    [Fact]
    public async Task DividendCoverageUsesActualOperatingCashFlowOnly()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(
            company,
            seedCycle,
            dayNumber: 1,
            configure: snapshot =>
            {
                snapshot.NetProfit = 100m;
                snapshot.OperatingCashFlow = 200m;
                snapshot.ExpectedDividendPerShare = 0.10m;
                snapshot.ExpectedDividendPool = 100m;
                snapshot.DividendCoverageRatio = 2m;
            });

        await Service(new ScriptedRandom(
                new[] { 0.5d }.Concat(Enumerable.Repeat(0.99d, 12))))
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.Equal(
            Math.Round(
                staged.OperatingCashFlow / staged.ExpectedDividendPool,
                6,
                MidpointRounding.AwayFromZero),
            staged.DividendCoverageRatio);
        Assert.NotEqual(
            Math.Round(
                staged.NetProfit / staged.ExpectedDividendPool,
                6,
                MidpointRounding.AwayFromZero),
            staged.DividendCoverageRatio);
    }

    [Fact]
    public async Task DividendRoundingPreservesConfiguredCashFlowCoverage()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(
            company,
            seedCycle,
            dayNumber: 1,
            configure: snapshot =>
            {
                snapshot.NetProfit = 100m;
                snapshot.OperatingCashFlow = 1m;
                snapshot.ExpectedDividendPerShare = 1m;
                snapshot.ExpectedDividendPool = 1_000m;
                snapshot.DividendCoverageRatio = 0.001m;
            });

        await Service(new ScriptedRandom(
                new[] { 0.5d }.Concat(Enumerable.Repeat(0.99d, 12))))
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.Equal(0.83m, staged.ExpectedDividendPool);
        Assert.Equal(
            Math.Round(
                staged.OperatingCashFlow / staged.ExpectedDividendPool,
                6,
                MidpointRounding.AwayFromZero),
            staged.DividendCoverageRatio);
        Assert.True(staged.DividendCoverageRatio >= 1.20m);
    }

    [Theory]
    [InlineData(2_000, "0.0025")]
    [InlineData(500, "0.01")]
    [InlineData(1_500, "0.003333")]
    public async Task ShareCountChangeRedenominatesPerShareWithoutChangingDividendPool(
        int currentIssuedShares,
        string expectedPerShareText)
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync(issuedShares: 1_000);
        await AddSnapshotAsync(
            company,
            seedCycle,
            dayNumber: 1,
            configure: snapshot =>
            {
                snapshot.ExpectedDividendPerShare = 0.005m;
                snapshot.ExpectedDividendPool = 5m;
                snapshot.DividendCoverageRatio = 2.4m;
            });
        company.IssuedSharesCount = currentIssuedShares;
        await context.SaveChangesAsync();

        await Service(new ScriptedRandom(
                new[] { 0.5d }.Concat(Enumerable.Repeat(0.99d, 12))))
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.Equal(5m, staged.ExpectedDividendPool);
        Assert.Equal(
            decimal.Parse(expectedPerShareText, System.Globalization.CultureInfo.InvariantCulture),
            staged.ExpectedDividendPerShare);
        Assert.Equal(CompanyFinancialMetric.None, staged.ChangedMetrics);
    }

    [Fact]
    public async Task ShareCountChangeStillAppliesDividendEconomicCaps()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var company = await AddCompanyAsync(issuedShares: 1_000);
        await AddSnapshotAsync(
            company,
            seedCycle,
            dayNumber: 1,
            configure: snapshot =>
            {
                snapshot.ExpectedDividendPerShare = 0.10m;
                snapshot.ExpectedDividendPool = 100m;
                snapshot.DividendCoverageRatio = 0.12m;
            });
        company.IssuedSharesCount = 2_000;
        await context.SaveChangesAsync();

        await Service(new ScriptedRandom(
                new[] { 0.5d }.Concat(Enumerable.Repeat(0.99d, 12))))
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.Equal(6m, staged.ExpectedDividendPool);
        Assert.Equal(0.003m, staged.ExpectedDividendPerShare);
        Assert.Equal(CompanyFinancialMetric.None, staged.ChangedMetrics);
    }

    [Theory]
    [InlineData(1_000, 0d, "0.01")]
    [InlineData(10_000, 0d, "0.01")]
    [InlineData(10_000, 0.99d, "0")]
    public async Task SelectedDividendCanRecoverFromZeroWithoutLosingDrawDiscipline(
        int issuedShares,
        double directionRoll,
        string expectedPoolText)
    {
        var dayOneCycle = await AddCycleAsync(dayNumber: 1, tradingCycleNumber: 2);
        var dayTwo = await AddTradingDayAsync(2);
        var recoveredCycle = await AddCycleAsync(dayTwo, tradingCycleNumber: 2);
        var middayCycle = await AddCycleAsync(dayTwo, MiddayTradingCycleNumber);
        var company = await AddCompanyAsync(issuedShares: issuedShares);
        await AddSnapshotAsync(
            company,
            dayOneCycle,
            dayNumber: 1,
            configure: snapshot =>
            {
                snapshot.NetProfit = -10m;
                snapshot.OperatingCashFlow = -10m;
                snapshot.ExpectedDividendPerShare = 0m;
                snapshot.ExpectedDividendPool = 0m;
                snapshot.DividendCoverageRatio = 0m;
                snapshot.ManagementProfitForecast = -10m;
                snapshot.ManagementOperatingCashFlowForecast = -10m;
            });
        await AddSnapshotAsync(
            company,
            recoveredCycle,
            dayNumber: 2,
            configure: snapshot =>
            {
                snapshot.NetProfit = 10m;
                snapshot.OperatingCashFlow = 12m;
                snapshot.ExpectedDividendPerShare = 0m;
                snapshot.ExpectedDividendPool = 0m;
                snapshot.DividendCoverageRatio = 0m;
            });
        var draws = new List<double> { 0.5d };
        draws.AddRange(Enumerable.Repeat(0.99d, 6));
        draws.AddRange([0d, directionRoll, 0d]);
        draws.AddRange(Enumerable.Repeat(0.99d, 5));
        var random = new ScriptedRandom(draws);

        await Service(random).ProcessForCycleAsync(middayCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.Equal(
            decimal.Parse(expectedPoolText, System.Globalization.CultureInfo.InvariantCulture),
            staged.ExpectedDividendPool);
        Assert.Equal(
            CompanyFinancialMetric.ExpectedDividendPerShare,
            staged.ChangedMetrics);
        Assert.Equal(0, random.Remaining);
    }

    [Fact]
    public async Task ScriptedDrawSegmentsAreAssignedByAscendingCompanyId()
    {
        var day = await AddTradingDayAsync(1);
        var seedCycle = await AddCycleAsync(day, tradingCycleNumber: 2);
        var openingCycle = await AddCycleAsync(day, tradingCycleNumber: 1);
        var lowerIdCompany = await AddCompanyAsync();
        var higherIdCompany = await AddCompanyAsync();
        await AddSnapshotAsync(lowerIdCompany, seedCycle, dayNumber: 1);
        await AddSnapshotAsync(higherIdCompany, seedCycle, dayNumber: 1);
        var draws = new List<double>
        {
            0.5d,
            0d, 0d, 0d,
        };
        draws.AddRange(Enumerable.Repeat(0.99d, 11));
        draws.AddRange(
        [
            0.5d,
            0d, 0.99d, 0d,
        ]);
        draws.AddRange(Enumerable.Repeat(0.99d, 11));

        await Service(new ScriptedRandom(draws))
            .ProcessForCycleAsync(openingCycle.Id, DateTime.UtcNow);

        var staged = context.ChangeTracker
            .Entries<CompanyFinancialSnapshot>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .OrderBy(snapshot => snapshot.CompanyId)
            .ToArray();
        Assert.Equal(2, staged.Length);
        Assert.Equal(lowerIdCompany.Id, staged[0].CompanyId);
        Assert.True(staged[0].Revenue > 100m);
        Assert.Equal(higherIdCompany.Id, staged[1].CompanyId);
        Assert.True(staged[1].Revenue < 100m);
    }

    [Fact]
    public async Task UpdateUsesLatestSnapshotByFinancialHistoryOrder()
    {
        var dayOne = await AddTradingDayAsync(1);
        var dayTwo = await AddTradingDayAsync(2);
        var dayThree = await AddTradingDayAsync(3);
        var dayTwoOpeningCycle = await AddCycleAsync(dayTwo, tradingCycleNumber: 1);
        var dayOneMiddayCycle = await AddCycleAsync(dayOne, MiddayTradingCycleNumber);
        var dayTwoSeedCycle = await AddCycleAsync(dayTwo, tradingCycleNumber: 2);
        var targetCycle = await AddCycleAsync(dayThree, tradingCycleNumber: 1);
        var company = await AddCompanyAsync();
        await AddSnapshotAsync(
            company,
            dayTwoOpeningCycle,
            dayNumber: 2,
            configure: snapshot =>
            {
                snapshot.Moment = CompanyFinancialSnapshotMoment.DayOpening;
                snapshot.Revenue = 500m;
                snapshot.ManagementRevenueForecast = 500m;
                snapshot.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            });
        await AddSnapshotAsync(
            company,
            dayOneMiddayCycle,
            dayNumber: 1,
            configure: snapshot =>
            {
                snapshot.Moment = CompanyFinancialSnapshotMoment.Midday;
                snapshot.Revenue = 300m;
                snapshot.ManagementRevenueForecast = 300m;
                snapshot.CreatedAt = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
            });
        await AddSnapshotAsync(
            company,
            dayTwoSeedCycle,
            dayNumber: 2,
            configure: snapshot =>
            {
                snapshot.Revenue = 400m;
                snapshot.ManagementRevenueForecast = 400m;
                snapshot.CreatedAt = new DateTime(2026, 12, 2, 0, 0, 0, DateTimeKind.Utc);
            });
        var draws = new List<double>
        {
            0.5d,
            0d, 0d, 0d,
        };
        draws.AddRange(Enumerable.Repeat(0.99d, 11));

        await Service(new ScriptedRandom(draws))
            .ProcessForCycleAsync(targetCycle.Id, DateTime.UtcNow);

        var staged = Assert.Single(
                context.ChangeTracker.Entries<CompanyFinancialSnapshot>(),
                entry => entry.State == EntityState.Added)
            .Entity;
        Assert.Equal(502.50m, staged.Revenue);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private CompanyFinancialService Service(
        Random random,
        bool enabled = true,
        CompanyFinancialOptions? financialOptions = null,
        RandomChanceRatesOptions? chanceRates = null)
    {
        financialOptions ??= new CompanyFinancialOptions();
        financialOptions.Enabled = enabled;
        return new CompanyFinancialService(
            context,
            Options.Create(financialOptions),
            Options.Create(chanceRates ?? new RandomChanceRatesOptions()),
            Options.Create(new TradingClockOptions
            {
                TradingCyclesPerDay = TradingCyclesPerDay,
            }),
            random,
            new CompanyFinancialScorer(Options.Create(financialOptions)));
    }

    private async Task<TradingDay> AddTradingDayAsync(int dayNumber)
    {
        var day = new TradingDay
        {
            DayNumber = dayNumber,
            State = TradingSessionState.Trading,
            OpenedInCycleId = 0,
        };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();
        return day;
    }

    private async Task<MarketCycle> AddCycleAsync(int dayNumber, int tradingCycleNumber)
    {
        var day = await AddTradingDayAsync(dayNumber);
        return await AddCycleAsync(day, tradingCycleNumber);
    }

    private async Task<MarketCycle> AddCycleAsync(
        TradingDay day,
        int tradingCycleNumber)
    {
        var cycle = new MarketCycle
        {
            CycleNumber = await context.MarketCycles.CountAsync() + 1,
            TradingDayId = day.Id,
            TradingCycleNumber = tradingCycleNumber,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();
        return cycle;
    }

    private async Task<Company> AddCompanyAsync(
        int issuedShares = 1_000,
        decimal cashBalance = 1_000m,
        int sentimentValue = 0,
        int? closedInCycleId = null)
    {
        var industry = new Industry
        {
            Name = $"Industry {Guid.NewGuid():N}",
            SentimentValue = sentimentValue,
        };
        context.Industries.Add(industry);
        await context.SaveChangesAsync();
        var company = new Company
        {
            Name = $"Financial issuer {Guid.NewGuid():N}",
            IndustryId = industry.Id,
            IssuedSharesCount = issuedShares,
            CashBalance = cashBalance,
            ClosedInCycleId = closedInCycleId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        return company;
    }

    private async Task<CompanyFinancialSnapshot> AddSnapshotAsync(
        Company company,
        MarketCycle cycle,
        int dayNumber,
        Action<CompanyFinancialSnapshot>? configure = null)
    {
        var snapshot = new CompanyFinancialSnapshot
        {
            CompanyId = company.Id,
            CreatedInCycleId = cycle.Id,
            TradingDayNumber = dayNumber,
            Moment = CompanyFinancialSnapshotMoment.Seed,
            CreatedAt = DateTime.UtcNow,
            Revenue = 100m,
            NetProfit = 10m,
            OperatingCashFlow = 12m,
            TotalAssets = 200m,
            TotalLiabilities = 80m,
            TotalDebt = 40m,
            ExpectedDividendPerShare = 0.005m,
            ExpectedDividendPool = 5m,
            DividendCoverageRatio = 2.4m,
            BusinessRiskScore = 50m,
            ManagementRevenueForecast = 102m,
            ManagementProfitForecast = 10.2m,
            ManagementOperatingCashFlowForecast = 12.2m,
            ManagementOutlook = ManagementOutlook.Positive,
            ManagementConfidenceScore = 60m,
            ProfitabilityScore = 60m,
            ProfitabilityLevel = CompanyMetricLevel.Medium,
            StabilityScore = 50m,
            FinancialVolatilityLevel = CompanyMetricLevel.Medium,
            ClosureRiskScore = 40m,
            ClosureRiskLevel = CompanyMetricLevel.Medium,
            ChangedMetrics = CompanyFinancialMetric.All,
        };
        configure?.Invoke(snapshot);
        context.CompanyFinancialSnapshots.Add(snapshot);
        await context.SaveChangesAsync();
        return snapshot;
    }

    private static CompanyFinancialState StateOf(CompanyFinancialSnapshot snapshot) =>
        new(
            snapshot.Revenue,
            snapshot.NetProfit,
            snapshot.OperatingCashFlow,
            snapshot.TotalAssets,
            snapshot.TotalLiabilities,
            snapshot.TotalDebt,
            snapshot.ExpectedDividendPerShare,
            snapshot.ExpectedDividendPool,
            snapshot.DividendCoverageRatio,
            snapshot.BusinessRiskScore,
            snapshot.ManagementRevenueForecast,
            snapshot.ManagementProfitForecast,
            snapshot.ManagementOperatingCashFlowForecast,
            snapshot.ManagementConfidenceScore);

    private static void AssertWithinDeviation(
        decimal actual,
        decimal forecast,
        decimal maximumDeviation)
    {
        var scale = Math.Max(Math.Abs(actual), 0.000001m);
        Assert.InRange(
            Math.Abs(forecast - actual) / scale,
            0m,
            maximumDeviation);
    }

    private sealed class ScriptedRandom(IEnumerable<double> draws) : Random
    {
        private readonly Queue<double> draws = new(draws);

        public int Remaining => draws.Count;

        public override double NextDouble() => draws.Dequeue();
    }
}
