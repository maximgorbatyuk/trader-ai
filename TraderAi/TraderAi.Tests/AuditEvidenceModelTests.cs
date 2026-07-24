using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Tests;

public sealed class AuditEvidenceModelTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public AuditEvidenceModelTests()
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
    public void RiskRatingsKeepStableValuesAndAddStable()
    {
        Assert.Equal(0, (int)CompanyRiskRating.LowRisk);
        Assert.Equal(1, (int)CompanyRiskRating.HighRisk);
        Assert.Equal(3, (int)CompanyRiskRating.RaisedExpectations);
        Assert.Equal(4, (int)CompanyRiskRating.ExtraRaisedExpectations);
        Assert.Equal(5, (int)CompanyRiskRating.Stable);
        Assert.Equal(
            [
                nameof(CompanyRiskRating.LowRisk),
                nameof(CompanyRiskRating.HighRisk),
                nameof(CompanyRiskRating.RaisedExpectations),
                nameof(CompanyRiskRating.ExtraRaisedExpectations),
                nameof(CompanyRiskRating.Stable),
            ],
            Enum.GetNames<CompanyRiskRating>());
    }

    [Fact]
    public void ModelEnforcesOneEvidencePerRatingAndOneSummaryItemPerCompany()
    {
        var evidenceType = context.Model.FindEntityType(typeof(CompanyAuditEvidence));
        Assert.NotNull(evidenceType);
        Assert.Equal(
            [nameof(CompanyAuditEvidence.CompanyRatingId)],
            evidenceType.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Contains(evidenceType.GetForeignKeys(), foreignKey =>
            foreignKey.IsUnique
            && foreignKey.PrincipalEntityType.ClrType == typeof(CompanyRating));

        var itemType = context.Model.FindEntityType(typeof(PortfolioAuditSummaryItem));
        Assert.NotNull(itemType);
        Assert.Contains(itemType.GetIndexes(), index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(PortfolioAuditSummaryItem.PortfolioAuditSummaryId), nameof(PortfolioAuditSummaryItem.CompanyId)]));
    }

    [Fact]
    public async Task FundedDividendCannotExceedDeclaredDividend()
    {
        var company = await AddCompanyAsync();
        context.CompanyDividendEvents.Add(new CompanyDividendEvent
        {
            CompanyId = company.Id,
            DeclaredAmount = 100m,
            FundedAmount = 100.01m,
            FundingOutcome = DividendFundingOutcome.Paid,
            IssuerCashBeforeFunding = 500m,
            CreatedInCycleId = 1,
            TradingDayNumber = 1,
            CreatedAt = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task EvidencePercentagesAndScoresRetainSixDecimalPlaces()
    {
        var company = await AddCompanyAsync();
        var auditor = new Auditor
        {
            Name = "Evidence Audit",
            Description = "Tests immutable audit evidence.",
            CreatedAt = DateTime.UtcNow,
        };
        context.Auditors.Add(auditor);
        await context.SaveChangesAsync();
        var rating = new CompanyRating
        {
            CompanyId = company.Id,
            AuditorId = auditor.Id,
            Rating = CompanyRiskRating.Stable,
            CreatedInCycleId = 1,
            CreatedAt = DateTime.UtcNow,
        };
        context.CompanyRatings.Add(rating);
        await context.SaveChangesAsync();
        context.CompanyAuditEvidence.Add(new CompanyAuditEvidence
        {
            CompanyRatingId = rating.Id,
            CompanyId = company.Id,
            EvaluationStartTradingDayNumber = 1,
            EvaluationEndTradingDayNumber = 2,
            EffectiveTradingDayNumber = 3,
            AdjustedReturnPercent = 12.345678m,
            MaximumAdjustedCycleMovePercent = 4.567891m,
            FreeShareDilutionPercent = 2.345678m,
            DividendCoverageRatio = 1.234567m,
            IndustryTrend = IndustryTrend.Plateau,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var stored = await context.CompanyAuditEvidence.SingleAsync();
        Assert.Equal(12.345678m, stored.AdjustedReturnPercent);
        Assert.Equal(4.567891m, stored.MaximumAdjustedCycleMovePercent);
        Assert.Equal(2.345678m, stored.FreeShareDilutionPercent);
        Assert.Equal(1.234567m, stored.DividendCoverageRatio);

        var entityType = context.Model.FindEntityType(typeof(CompanyAuditEvidence))!;
        Assert.Equal(6, entityType.FindProperty(nameof(CompanyAuditEvidence.AdjustedReturnPercent))!.GetScale());
        Assert.Equal(6, entityType.FindProperty(nameof(CompanyAuditEvidence.DividendCoverageRatio))!.GetScale());
    }

    [Fact]
    public async Task EvidenceCanReferenceTheFinancialSnapshotUsedByTheAudit()
    {
        var company = await AddCompanyAsync();
        var cycle = new MarketCycle
        {
            CycleNumber = 1,
            TradingCycleNumber = 1,
            Status = CycleStatus.Completed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        var auditor = new Auditor
        {
            Name = "Financial evidence audit",
            Description = "Uses the financial state available to the auditor.",
            CreatedAt = DateTime.UtcNow,
        };
        context.AddRange(cycle, auditor);
        await context.SaveChangesAsync();
        var snapshot = new CompanyFinancialSnapshot
        {
            CompanyId = company.Id,
            CreatedInCycleId = cycle.Id,
            TradingDayNumber = 1,
            Moment = CompanyFinancialSnapshotMoment.DayOpening,
            CreatedAt = DateTime.UtcNow,
            Revenue = 10000m,
            NetProfit = 1000m,
            OperatingCashFlow = 1200m,
            TotalAssets = 20000m,
            TotalLiabilities = 8000m,
            TotalDebt = 5000m,
            DividendCoverageRatio = 4m,
            BusinessRiskScore = 20m,
            ManagementRevenueForecast = 11000m,
            ManagementProfitForecast = 1200m,
            ManagementOperatingCashFlowForecast = 1400m,
            ManagementOutlook = ManagementOutlook.Positive,
            ManagementConfidenceScore = 80m,
            ProfitabilityScore = 70m,
            ProfitabilityLevel = CompanyMetricLevel.High,
            StabilityScore = 75m,
            FinancialVolatilityLevel = CompanyMetricLevel.Low,
            ClosureRiskScore = 15m,
            ClosureRiskLevel = CompanyMetricLevel.Low,
        };
        context.CompanyFinancialSnapshots.Add(snapshot);
        await context.SaveChangesAsync();
        var rating = new CompanyRating
        {
            CompanyId = company.Id,
            AuditorId = auditor.Id,
            Rating = CompanyRiskRating.RaisedExpectations,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        };
        context.CompanyRatings.Add(rating);
        await context.SaveChangesAsync();
        context.CompanyAuditEvidence.Add(new CompanyAuditEvidence
        {
            CompanyRatingId = rating.Id,
            CompanyId = company.Id,
            CompanyFinancialSnapshotId = snapshot.Id,
            EvaluationStartTradingDayNumber = 1,
            EvaluationEndTradingDayNumber = 2,
            EffectiveTradingDayNumber = 3,
            ProfitabilityFactorScore = 4,
            StabilityFactorScore = 3,
            ClosureRiskFactorScore = 2,
            ManagementOutlookFactorScore = 4,
            IndustryTrend = IndustryTrend.Rising,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var stored = await context.CompanyAuditEvidence
            .Include(evidence => evidence.CompanyFinancialSnapshot)
            .SingleAsync();

        Assert.Equal(snapshot.Id, stored.CompanyFinancialSnapshotId);
        Assert.NotNull(stored.CompanyFinancialSnapshot);
        Assert.Equal(4, stored.ProfitabilityFactorScore);
        Assert.Equal(3, stored.StabilityFactorScore);
        Assert.Equal(2, stored.ClosureRiskFactorScore);
        Assert.Equal(4, stored.ManagementOutlookFactorScore);

        var foreignKey = context.Model.FindEntityType(typeof(CompanyAuditEvidence))!
            .GetForeignKeys()
            .Single(candidate => candidate.PrincipalEntityType.ClrType == typeof(CompanyFinancialSnapshot));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.False(foreignKey.IsRequired);
    }

    [Fact]
    public async Task EvidenceCannotReferenceAnotherCompanysFinancialSnapshot()
    {
        var (company, otherCompany, cycle, rating) = await AddAuditSetupAsync();
        var otherSnapshot = CreateValidSnapshot(otherCompany.Id, cycle.Id);
        context.CompanyFinancialSnapshots.Add(otherSnapshot);
        await context.SaveChangesAsync();
        context.CompanyAuditEvidence.Add(new CompanyAuditEvidence
        {
            CompanyRatingId = rating.Id,
            CompanyId = company.Id,
            CompanyFinancialSnapshotId = otherSnapshot.Id,
            EvaluationStartTradingDayNumber = 1,
            EvaluationEndTradingDayNumber = 2,
            EffectiveTradingDayNumber = 3,
            IndustryTrend = IndustryTrend.Plateau,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task EvidenceCannotReferenceAnotherCompanysDividendEvent()
    {
        var (company, otherCompany, cycle, rating) = await AddAuditSetupAsync();
        var otherDividend = new CompanyDividendEvent
        {
            CompanyId = otherCompany.Id,
            DeclaredAmount = 100m,
            FundedAmount = 100m,
            FundingOutcome = DividendFundingOutcome.Paid,
            IssuerCashBeforeFunding = 500m,
            CreatedInCycleId = cycle.Id,
            TradingDayNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        context.CompanyDividendEvents.Add(otherDividend);
        await context.SaveChangesAsync();
        context.CompanyAuditEvidence.Add(new CompanyAuditEvidence
        {
            CompanyRatingId = rating.Id,
            CompanyId = company.Id,
            LatestDividendEventId = otherDividend.Id,
            EvaluationStartTradingDayNumber = 1,
            EvaluationEndTradingDayNumber = 2,
            EffectiveTradingDayNumber = 3,
            IndustryTrend = IndustryTrend.Plateau,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task CompanyDeletionIsRestrictedWhileAuditHistoryExists()
    {
        var company = await AddCompanyAsync();
        var auditor = new Auditor
        {
            Name = "History Audit",
            Description = "Keeps the issuer history.",
            CreatedAt = DateTime.UtcNow,
        };
        context.Auditors.Add(auditor);
        await context.SaveChangesAsync();
        context.CompanyRatings.Add(new CompanyRating
        {
            CompanyId = company.Id,
            AuditorId = auditor.Id,
            Rating = CompanyRiskRating.Stable,
            CreatedInCycleId = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        context.Companies.Remove(await context.Companies.SingleAsync(row => row.Id == company.Id));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task ResetOrderCanDeleteEvidenceChildrenBeforeTheirParents()
    {
        var company = await AddCompanyAsync();
        var auditor = new Auditor
        {
            Name = "Reset Audit",
            Description = "Tests reset deletion order.",
            CreatedAt = DateTime.UtcNow,
        };
        var news = new NewsPost
        {
            Title = "Portfolio audit",
            Content = "Summary",
            PublishedInCycleId = 1,
            PublishedAt = DateTime.UtcNow,
        };
        context.AddRange(auditor, news);
        await context.SaveChangesAsync();
        var dividend = new CompanyDividendEvent
        {
            CompanyId = company.Id,
            DeclaredAmount = 100m,
            FundedAmount = 80m,
            FundingOutcome = DividendFundingOutcome.Reduced,
            IssuerCashBeforeFunding = 80m,
            CreatedInCycleId = 1,
            TradingDayNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        var rating = new CompanyRating
        {
            CompanyId = company.Id,
            AuditorId = auditor.Id,
            Rating = CompanyRiskRating.LowRisk,
            CreatedInCycleId = 1,
            CreatedAt = DateTime.UtcNow,
        };
        context.AddRange(dividend, rating);
        await context.SaveChangesAsync();
        var summary = new PortfolioAuditSummary
        {
            NewsPostId = news.Id,
            EvaluationStartTradingDayNumber = 1,
            EvaluationEndTradingDayNumber = 2,
            EffectiveTradingDayNumber = 3,
            LowRiskCount = 1,
            AverageScore = -2m,
            OverallDirection = PortfolioAuditDirection.Negative,
            CreatedAt = DateTime.UtcNow,
        };
        context.PortfolioAuditSummaries.Add(summary);
        context.CompanyAuditEvidence.Add(new CompanyAuditEvidence
        {
            CompanyRatingId = rating.Id,
            CompanyId = company.Id,
            EvaluationStartTradingDayNumber = 1,
            EvaluationEndTradingDayNumber = 2,
            EffectiveTradingDayNumber = 3,
            LatestDividendEventId = dividend.Id,
            IndustryTrend = IndustryTrend.Falling,
        });
        await context.SaveChangesAsync();
        context.PortfolioAuditSummaryItems.Add(new PortfolioAuditSummaryItem
        {
            PortfolioAuditSummaryId = summary.Id,
            CompanyId = company.Id,
            CompanyRatingId = rating.Id,
            PlayerQuantity = 10,
            ManagedFundQuantity = 20,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.PortfolioAuditSummaryItems.ExecuteDeleteAsync();
        await context.PortfolioAuditSummaries.ExecuteDeleteAsync();
        await context.CompanyAuditEvidence.ExecuteDeleteAsync();
        await context.CompanyDividendEvents.ExecuteDeleteAsync();
        await context.CompanyRatings.ExecuteDeleteAsync();
        await context.NewsPosts.ExecuteDeleteAsync();
        await context.Companies.ExecuteDeleteAsync();

        Assert.Empty(await context.PortfolioAuditSummaryItems.ToListAsync());
        Assert.Empty(await context.CompanyAuditEvidence.ToListAsync());
        Assert.Empty(await context.CompanyDividendEvents.ToListAsync());
        Assert.Empty(await context.CompanyRatings.ToListAsync());
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private async Task<Company> AddCompanyAsync()
    {
        var company = new Company
        {
            Name = $"Issuer {Guid.NewGuid():N}",
            IssuedSharesCount = 1000,
            CashBalance = 1000m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        return company;
    }

    private async Task<(Company Company, Company OtherCompany, MarketCycle Cycle, CompanyRating Rating)> AddAuditSetupAsync()
    {
        var company = await AddCompanyAsync();
        var otherCompany = await AddCompanyAsync();
        var cycle = new MarketCycle
        {
            CycleNumber = 1,
            TradingCycleNumber = 1,
            Status = CycleStatus.Completed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        var auditor = new Auditor
        {
            Name = "Company-aware audit",
            Description = "Keeps evidence inside the audited company.",
            CreatedAt = DateTime.UtcNow,
        };
        context.AddRange(cycle, auditor);
        await context.SaveChangesAsync();
        var rating = new CompanyRating
        {
            CompanyId = company.Id,
            AuditorId = auditor.Id,
            Rating = CompanyRiskRating.Stable,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        };
        context.CompanyRatings.Add(rating);
        await context.SaveChangesAsync();
        return (company, otherCompany, cycle, rating);
    }

    private static CompanyFinancialSnapshot CreateValidSnapshot(int companyId, int cycleId) =>
        new()
        {
            CompanyId = companyId,
            CreatedInCycleId = cycleId,
            TradingDayNumber = 1,
            Moment = CompanyFinancialSnapshotMoment.DayOpening,
            CreatedAt = DateTime.UtcNow,
            Revenue = 10000m,
            NetProfit = 1000m,
            OperatingCashFlow = 1200m,
            TotalAssets = 20000m,
            TotalLiabilities = 8000m,
            TotalDebt = 5000m,
            DividendCoverageRatio = 4m,
            BusinessRiskScore = 20m,
            ManagementRevenueForecast = 11000m,
            ManagementProfitForecast = 1200m,
            ManagementOperatingCashFlowForecast = 1400m,
            ManagementOutlook = ManagementOutlook.Positive,
            ManagementConfidenceScore = 80m,
            ProfitabilityScore = 70m,
            ProfitabilityLevel = CompanyMetricLevel.High,
            StabilityScore = 75m,
            FinancialVolatilityLevel = CompanyMetricLevel.Low,
            ClosureRiskScore = 15m,
            ClosureRiskLevel = CompanyMetricLevel.Low,
        };
}
