using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Tests;

public sealed class CompanyFinancialPersistenceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public CompanyFinancialPersistenceTests()
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
    public void FinancialEnumsKeepStableStoredValues()
    {
        Assert.Equal(0, (int)CompanyFinancialSnapshotMoment.Seed);
        Assert.Equal(1, (int)CompanyFinancialSnapshotMoment.DayOpening);
        Assert.Equal(2, (int)CompanyFinancialSnapshotMoment.Midday);

        Assert.Equal(0, (int)CompanyMetricLevel.Low);
        Assert.Equal(1, (int)CompanyMetricLevel.Medium);
        Assert.Equal(2, (int)CompanyMetricLevel.High);

        Assert.Equal(0, (int)ManagementOutlook.Neutral);
        Assert.Equal(1, (int)ManagementOutlook.Positive);
        Assert.Equal(2, (int)ManagementOutlook.Negative);

        Assert.Equal(0, (int)CompanyFinancialMetric.None);
        Assert.Equal(1, (int)CompanyFinancialMetric.Revenue);
        Assert.Equal(2, (int)CompanyFinancialMetric.NetProfit);
        Assert.Equal(4, (int)CompanyFinancialMetric.OperatingCashFlow);
        Assert.Equal(8, (int)CompanyFinancialMetric.TotalAssets);
        Assert.Equal(16, (int)CompanyFinancialMetric.TotalLiabilities);
        Assert.Equal(32, (int)CompanyFinancialMetric.TotalDebt);
        Assert.Equal(64, (int)CompanyFinancialMetric.ExpectedDividendPerShare);
        Assert.Equal(128, (int)CompanyFinancialMetric.BusinessRisk);
        Assert.Equal(256, (int)CompanyFinancialMetric.ManagementRevenueForecast);
        Assert.Equal(512, (int)CompanyFinancialMetric.ManagementProfitForecast);
        Assert.Equal(1024, (int)CompanyFinancialMetric.ManagementOperatingCashFlowForecast);
        Assert.Equal(2048, (int)CompanyFinancialMetric.ManagementConfidence);
        Assert.Equal(4095, (int)CompanyFinancialMetric.All);
    }

    [Fact]
    public async Task SnapshotRoundTripsDecimalsFlagsAndOptionalRelations()
    {
        var (company, cycle) = await AddCompanyAndCycleAsync();
        var dividend = new CompanyDividendEvent
        {
            CompanyId = company.Id,
            DeclaredAmount = 1234.56m,
            FundedAmount = 1200.12m,
            FundingOutcome = DividendFundingOutcome.Reduced,
            IssuerCashBeforeFunding = 5000m,
            CreatedInCycleId = cycle.Id,
            TradingDayNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        context.CompanyDividendEvents.Add(dividend);
        await context.SaveChangesAsync();

        context.CompanyFinancialSnapshots.Add(CreateValidSnapshot(company.Id, cycle.Id, snapshot =>
        {
            snapshot.Revenue = 9876543210.12m;
            snapshot.NetProfit = -1234567.89m;
            snapshot.OperatingCashFlow = 3456789.01m;
            snapshot.ExpectedDividendPerShare = 1.234567m;
            snapshot.ExpectedDividendPool = 123456.78m;
            snapshot.DividendCoverageRatio = 2.345678m;
            snapshot.LatestDividendEventId = dividend.Id;
            snapshot.BusinessRiskScore = 23.456789m;
            snapshot.ManagementRevenueForecast = 10000000000.12m;
            snapshot.ManagementProfitForecast = -1000000.34m;
            snapshot.ManagementOperatingCashFlowForecast = -765432.10m;
            snapshot.ManagementConfidenceScore = 78.901234m;
            snapshot.ProfitabilityScore = 67.891234m;
            snapshot.StabilityScore = 76.543219m;
            snapshot.ClosureRiskScore = 12.345678m;
            snapshot.ChangedMetrics = CompanyFinancialMetric.Revenue
                | CompanyFinancialMetric.NetProfit
                | CompanyFinancialMetric.ManagementConfidence;
        }));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var stored = await context.CompanyFinancialSnapshots
            .Include(snapshot => snapshot.LatestDividendEvent)
            .SingleAsync();

        Assert.Equal(9876543210.12m, stored.Revenue);
        Assert.Equal(-1234567.89m, stored.NetProfit);
        Assert.Equal(1.234567m, stored.ExpectedDividendPerShare);
        Assert.Equal(2.345678m, stored.DividendCoverageRatio);
        Assert.Equal(23.456789m, stored.BusinessRiskScore);
        Assert.Equal(78.901234m, stored.ManagementConfidenceScore);
        Assert.Equal(67.891234m, stored.ProfitabilityScore);
        Assert.Equal(76.543219m, stored.StabilityScore);
        Assert.Equal(12.345678m, stored.ClosureRiskScore);
        Assert.Equal(
            CompanyFinancialMetric.Revenue
                | CompanyFinancialMetric.NetProfit
                | CompanyFinancialMetric.ManagementConfidence,
            stored.ChangedMetrics);
        Assert.Equal(dividend.Id, stored.LatestDividendEventId);
        Assert.NotNull(stored.LatestDividendEvent);

        var entityType = context.Model.FindEntityType(typeof(CompanyFinancialSnapshot))!;
        Assert.Equal(6, entityType.FindProperty(nameof(CompanyFinancialSnapshot.DividendCoverageRatio))!.GetScale());
        Assert.Equal(6, entityType.FindProperty(nameof(CompanyFinancialSnapshot.ProfitabilityScore))!.GetScale());
    }

    [Fact]
    public async Task SeedAndDayOpeningCanExistForSameCompanyAndDay()
    {
        var (company, cycle) = await AddCompanyAndCycleAsync();
        context.CompanyFinancialSnapshots.AddRange(
            CreateValidSnapshot(company.Id, cycle.Id),
            CreateValidSnapshot(company.Id, cycle.Id, snapshot =>
            {
                snapshot.Moment = CompanyFinancialSnapshotMoment.DayOpening;
                snapshot.CreatedAt = DateTime.UtcNow.AddSeconds(1);
            }));

        await context.SaveChangesAsync();

        Assert.Equal(2, await context.CompanyFinancialSnapshots.CountAsync());
    }

    [Fact]
    public async Task DuplicateCompanyDayAndMomentIsRejected()
    {
        var (company, cycle) = await AddCompanyAndCycleAsync();
        context.CompanyFinancialSnapshots.AddRange(
            CreateValidSnapshot(company.Id, cycle.Id),
            CreateValidSnapshot(company.Id, cycle.Id, snapshot =>
                snapshot.CreatedAt = DateTime.UtcNow.AddSeconds(1)));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Theory]
    [InlineData("day")]
    [InlineData("revenue")]
    [InlineData("assets")]
    [InlineData("liabilities")]
    [InlineData("debt")]
    [InlineData("debt-above-liabilities")]
    [InlineData("dividend-per-share")]
    [InlineData("dividend-pool")]
    [InlineData("coverage")]
    [InlineData("business-risk")]
    [InlineData("confidence")]
    [InlineData("profitability")]
    [InlineData("stability")]
    [InlineData("closure-risk")]
    public async Task InvalidFinancialValuesAreRejected(string invalidField)
    {
        var (company, cycle) = await AddCompanyAndCycleAsync();
        var snapshot = CreateValidSnapshot(company.Id, cycle.Id);
        switch (invalidField)
        {
            case "day":
                snapshot.TradingDayNumber = 0;
                break;
            case "revenue":
                snapshot.Revenue = -0.01m;
                break;
            case "assets":
                snapshot.TotalAssets = -0.01m;
                break;
            case "liabilities":
                snapshot.TotalLiabilities = -0.01m;
                break;
            case "debt":
                snapshot.TotalDebt = -0.01m;
                break;
            case "debt-above-liabilities":
                snapshot.TotalDebt = snapshot.TotalLiabilities + 0.01m;
                break;
            case "dividend-per-share":
                snapshot.ExpectedDividendPerShare = -0.000001m;
                break;
            case "dividend-pool":
                snapshot.ExpectedDividendPool = -0.01m;
                break;
            case "coverage":
                snapshot.DividendCoverageRatio = -0.000001m;
                break;
            case "business-risk":
                snapshot.BusinessRiskScore = 100.000001m;
                break;
            case "confidence":
                snapshot.ManagementConfidenceScore = -0.000001m;
                break;
            case "profitability":
                snapshot.ProfitabilityScore = 100.000001m;
                break;
            case "stability":
                snapshot.StabilityScore = -0.000001m;
                break;
            case "closure-risk":
                snapshot.ClosureRiskScore = 100.000001m;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidField));
        }
        context.CompanyFinancialSnapshots.Add(snapshot);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task NegativeProfitCashFlowAndForecastsAreAllowed()
    {
        var (company, cycle) = await AddCompanyAndCycleAsync();
        context.CompanyFinancialSnapshots.Add(CreateValidSnapshot(company.Id, cycle.Id, snapshot =>
        {
            snapshot.NetProfit = -500m;
            snapshot.OperatingCashFlow = -750m;
            snapshot.ManagementProfitForecast = -600m;
            snapshot.ManagementOperatingCashFlowForecast = -800m;
            snapshot.TotalAssets = 1000m;
            snapshot.TotalLiabilities = 1200m;
            snapshot.TotalDebt = 700m;
        }));

        await context.SaveChangesAsync();

        Assert.Single(await context.CompanyFinancialSnapshots.ToListAsync());
    }

    [Fact]
    public void SnapshotUsesRestrictedHistoryRelationshipsAndExpectedIndexes()
    {
        var entityType = context.Model.FindEntityType(typeof(CompanyFinancialSnapshot))!;

        Assert.Contains(entityType.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(Company)
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(entityType.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(MarketCycle)
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(entityType.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(CompanyDividendEvent)
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(entityType.GetIndexes(), index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(
                [
                    nameof(CompanyFinancialSnapshot.CompanyId),
                    nameof(CompanyFinancialSnapshot.TradingDayNumber),
                    nameof(CompanyFinancialSnapshot.Moment),
                ]));
        Assert.Contains(entityType.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
                [
                    nameof(CompanyFinancialSnapshot.CompanyId),
                    nameof(CompanyFinancialSnapshot.CreatedInCycleId),
                ]));
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private async Task<(Company Company, MarketCycle Cycle)> AddCompanyAndCycleAsync()
    {
        var company = new Company
        {
            Name = $"Financial issuer {Guid.NewGuid():N}",
            IssuedSharesCount = 1000,
            CashBalance = 1000m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var cycle = new MarketCycle
        {
            CycleNumber = 1,
            TradingCycleNumber = 1,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        context.AddRange(company, cycle);
        await context.SaveChangesAsync();
        return (company, cycle);
    }

    private static CompanyFinancialSnapshot CreateValidSnapshot(
        int companyId,
        int cycleId,
        Action<CompanyFinancialSnapshot>? configure = null)
    {
        var snapshot = new CompanyFinancialSnapshot
        {
            CompanyId = companyId,
            CreatedInCycleId = cycleId,
            TradingDayNumber = 1,
            Moment = CompanyFinancialSnapshotMoment.Seed,
            CreatedAt = DateTime.UtcNow,
            Revenue = 10000m,
            NetProfit = 1200m,
            OperatingCashFlow = 1500m,
            TotalAssets = 20000m,
            TotalLiabilities = 8000m,
            TotalDebt = 5000m,
            ExpectedDividendPerShare = 0.25m,
            ExpectedDividendPool = 250m,
            DividendCoverageRatio = 4.8m,
            BusinessRiskScore = 20m,
            ManagementRevenueForecast = 11000m,
            ManagementProfitForecast = 1400m,
            ManagementOperatingCashFlowForecast = 1600m,
            ManagementOutlook = ManagementOutlook.Positive,
            ManagementConfidenceScore = 75m,
            ProfitabilityScore = 70m,
            ProfitabilityLevel = CompanyMetricLevel.High,
            StabilityScore = 80m,
            FinancialVolatilityLevel = CompanyMetricLevel.Low,
            ClosureRiskScore = 10m,
            ClosureRiskLevel = CompanyMetricLevel.Low,
            ChangedMetrics = CompanyFinancialMetric.All,
        };

        configure?.Invoke(snapshot);
        return snapshot;
    }
}
