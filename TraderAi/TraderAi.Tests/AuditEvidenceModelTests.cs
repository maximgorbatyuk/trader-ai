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
}
