using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AuditorServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly RecordingCommandInterceptor commands = new();
    private readonly Dictionary<int, TradingDay> days = [];
    private readonly Industry industry;

    public AuditorServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(commands)
            .Options;
        context = new AppDbContext(dbOptions);
        context.Database.EnsureCreated();

        industry = new Industry
        {
            Name = "Audit Industry",
            SentimentValue = 500,
            SentimentVolatility = 0.1m,
            SectorBeta = 1m,
        };
        context.Industries.Add(industry);
        context.SaveChanges();
    }

    [Fact]
    public async Task PortfolioSummaryCombinesPlayerAndManagedFundHoldingsFromCurrentBatch()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var sharedCompany = await AddCompanyAsync(day1, "Shared company");
        var fundCompany = await AddCompanyAsync(day1, "Fund company");
        var outsiderCompany = await AddCompanyAsync(day1, "Outsider company");
        var notDueCompany = await AddCompanyAsync(day2, "Not due company");
        foreach (var company in new[] { sharedCompany, fundCompany, outsiderCompany, notDueCompany })
        {
            await AddPriceAsync(company, 100m, company == notDueCompany ? day2 : day1);
            await AddPriceAsync(company, 100m, day2);
        }

        await AddFinancialAsync(
            sharedCompany,
            day2,
            CompanyFinancialSnapshotMoment.Midday,
            profitability: CompanyMetricLevel.High,
            volatility: CompanyMetricLevel.Low,
            closureRisk: CompanyMetricLevel.Low,
            outlook: ManagementOutlook.Positive,
            confidence: 100m);
        await AddFinancialAsync(
            fundCompany,
            day2,
            CompanyFinancialSnapshotMoment.Midday,
            coverage: 0.5m,
            profitability: CompanyMetricLevel.Low,
            volatility: CompanyMetricLevel.High,
            closureRisk: CompanyMetricLevel.High,
            outlook: ManagementOutlook.Negative,
            confidence: 100m);
        await AddFinancialAsync(outsiderCompany, day2, CompanyFinancialSnapshotMoment.Midday);

        var player = await AddParticipantAsync(ParticipantType.Player);
        var managedFundParticipant = await AddParticipantAsync(ParticipantType.CollectiveFund);
        var ordinaryFundParticipant = await AddParticipantAsync(ParticipantType.CollectiveFund);
        var ordinaryInvestor = await AddParticipantAsync(ParticipantType.Individual);
        await AddFundAsync(managedFundParticipant, player, isPlayerManaged: true, day1);
        await AddFundAsync(ordinaryFundParticipant, ordinaryInvestor, isPlayerManaged: false, day1);

        var playerShared = await AddHoldingAsync(player, sharedCompany, 10);
        var fundShared = await AddHoldingAsync(managedFundParticipant, sharedCompany, 20);
        await AddHoldingAsync(managedFundParticipant, fundCompany, 30);
        await AddHoldingAsync(player, notDueCompany, 40);
        await AddHoldingAsync(ordinaryInvestor, outsiderCompany, 50);
        await AddHoldingAsync(ordinaryFundParticipant, outsiderCompany, 60);

        await ProcessAsync(day3);
        await ProcessAsync(day3);

        var summary = await context.PortfolioAuditSummaries
            .AsNoTracking()
            .Include(candidate => candidate.NewsPost)
            .Include(candidate => candidate.Items)
            .SingleAsync();
        Assert.Equal((1, 2, 3), (
            summary.EvaluationStartTradingDayNumber,
            summary.EvaluationEndTradingDayNumber,
            summary.EffectiveTradingDayNumber));
        Assert.Equal(1, summary.ExtraRaisedExpectationsCount);
        Assert.Equal(0, summary.RaisedExpectationsCount);
        Assert.Equal(0, summary.StableCount);
        Assert.Equal(0, summary.LowRiskCount);
        Assert.Equal(1, summary.HighRiskCount);
        Assert.Equal(-1m, summary.AverageScore);
        Assert.Equal(PortfolioAuditDirection.Neutral, summary.OverallDirection);
        Assert.Equal(day3.Id, summary.NewsPost!.PublishedInCycleId);
        Assert.Equal((NewsCategory)7, summary.NewsPost.Category);
        Assert.Equal(NewsImpactScope.None, summary.NewsPost.Scope);
        Assert.Null(summary.NewsPost.Direction);
        Assert.Null(summary.NewsPost.ImpactPercent);
        Assert.Null(summary.NewsPost.TargetCompanyId);
        Assert.Null(summary.NewsPost.ImpactAppliedInCycleId);

        Assert.Equal(2, summary.Items.Count);
        var sharedItem = Assert.Single(summary.Items, item => item.CompanyId == sharedCompany.Id);
        Assert.Equal(10, sharedItem.PlayerQuantity);
        Assert.Equal(20, sharedItem.ManagedFundQuantity);
        var fundItem = Assert.Single(summary.Items, item => item.CompanyId == fundCompany.Id);
        Assert.Equal(0, fundItem.PlayerQuantity);
        Assert.Equal(30, fundItem.ManagedFundQuantity);
        Assert.DoesNotContain(summary.Items, item => item.CompanyId == outsiderCompany.Id);
        Assert.DoesNotContain(summary.Items, item => item.CompanyId == notDueCompany.Id);

        var itemRatings = await context.PortfolioAuditSummaryItems
            .AsNoTracking()
            .Join(
                context.CompanyRatings.AsNoTracking(),
                item => item.CompanyRatingId,
                rating => rating.Id,
                (item, rating) => new { item.CompanyId, rating.Rating, rating.Evidence })
            .OrderBy(row => row.CompanyId)
            .ToListAsync();
        Assert.Contains(
            itemRatings,
            row => row.CompanyId == sharedCompany.Id
                && row.Rating == CompanyRiskRating.ExtraRaisedExpectations
                && row.Evidence!.TotalScore == 8);
        Assert.Contains(
            itemRatings,
            row => row.CompanyId == fundCompany.Id
                && row.Rating == CompanyRiskRating.HighRisk
                && row.Evidence!.TotalScore == -10);

        playerShared.Quantity = 99;
        fundShared.Quantity = 88;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var storedItems = await context.PortfolioAuditSummaryItems
            .AsNoTracking()
            .OrderBy(item => item.CompanyId)
            .ToListAsync();
        Assert.Equal(10, storedItems.Single(item => item.CompanyId == sharedCompany.Id).PlayerQuantity);
        Assert.Equal(20, storedItems.Single(item => item.CompanyId == sharedCompany.Id).ManagedFundQuantity);
    }

    [Fact]
    public async Task AuditWithoutPlayerPortfolioIntersectionDoesNotPublishNews()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 100m, day2);
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        var ordinaryInvestor = await AddParticipantAsync(ParticipantType.Individual);
        await AddHoldingAsync(ordinaryInvestor, company, 10);

        await ProcessAsync(day3);

        Assert.Single(await context.CompanyRatings.AsNoTracking().ToListAsync());
        Assert.Empty(await context.PortfolioAuditSummaries.AsNoTracking().ToListAsync());
        Assert.Empty(await context.NewsPosts.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(103, 2, PortfolioAuditDirection.Positive)]
    [InlineData(100, 0, PortfolioAuditDirection.Neutral)]
    [InlineData(96, -2, PortfolioAuditDirection.Negative)]
    public async Task PortfolioSummaryDirectionUsesInclusiveTwoPointBoundaries(
        int endingPrice,
        int expectedScore,
        PortfolioAuditDirection expectedDirection)
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, endingPrice, day2);
        var player = await AddParticipantAsync(ParticipantType.Player);
        await AddHoldingAsync(player, company, 1);

        await ProcessAsync(day3);

        var summary = await context.PortfolioAuditSummaries.AsNoTracking().SingleAsync();
        Assert.Equal(expectedScore, summary.AverageScore);
        Assert.Equal(expectedDirection, summary.OverallDirection);
    }

    [Fact]
    public async Task SeededCompanyUsesOpeningCycleAndTwoDayCadence()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3Opening = await AddCycleAsync(3, 1);
        var day3Later = await AddCycleAsync(3, 2);
        var day4 = await AddCycleAsync(4, 1);
        var day5 = await AddCycleAsync(5, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 100m, day2);
        await AddPriceAsync(company, 100m, day4);
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        await AddFinancialAsync(company, day4, CompanyFinancialSnapshotMoment.Midday);

        await ProcessAsync(day2);
        await ProcessAsync(day3Later);
        Assert.Empty(await context.CompanyRatings.AsNoTracking().ToListAsync());

        await ProcessAsync(day3Opening);
        await ProcessAsync(day3Opening);
        await ProcessAsync(day4);
        Assert.Single(await context.CompanyRatings.AsNoTracking().ToListAsync());

        await ProcessAsync(day5);
        var evidence = await context.CompanyAuditEvidence
            .AsNoTracking()
            .OrderBy(row => row.EffectiveTradingDayNumber)
            .ToListAsync();
        Assert.Equal(2, evidence.Count);
        Assert.Equal((1, 2, 3), (
            evidence[0].EvaluationStartTradingDayNumber,
            evidence[0].EvaluationEndTradingDayNumber,
            evidence[0].EffectiveTradingDayNumber));
        Assert.Equal((3, 4, 5), (
            evidence[1].EvaluationStartTradingDayNumber,
            evidence[1].EvaluationEndTradingDayNumber,
            evidence[1].EffectiveTradingDayNumber));
    }

    [Fact]
    public async Task RepeatedCallBeforeSaveStagesOneAudit()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 100m, day2);
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        var service = Service();

        await service.ProcessForCycleAsync(day3.Id, day3.CycleNumber, DateTime.UtcNow);
        await service.ProcessForCycleAsync(day3.Id, day3.CycleNumber, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Single(await context.CompanyRatings.AsNoTracking().ToListAsync());
        Assert.Single(await context.CompanyAuditEvidence.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DuplicateCompanyEffectiveDayEvidenceIsRejected()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 100m, day2);
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        await ProcessAsync(day3);
        var auditor = await context.Auditors.SingleAsync();
        var duplicate = new CompanyRating
        {
            CompanyId = company.Id,
            AuditorId = auditor.Id,
            Rating = CompanyRiskRating.LowRisk,
            CreatedInCycleId = day3.Id,
            CreatedAt = DateTime.UtcNow,
        };
        duplicate.Evidence = new CompanyAuditEvidence
        {
            CompanyRating = duplicate,
            CompanyId = company.Id,
            EvaluationStartTradingDayNumber = 1,
            EvaluationEndTradingDayNumber = 2,
            EffectiveTradingDayNumber = 3,
            IndustryTrend = IndustryTrend.Plateau,
        };
        context.CompanyRatings.Add(duplicate);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task ConfiguredOneDayIntervalAuditsOnFollowingDay()
    {
        var day4 = await AddCycleAsync(4, 1);
        var day5 = await AddCycleAsync(5, 1);
        var company = await AddCompanyAsync(day4);
        await AddPriceAsync(company, 100m, day4);
        await AddFinancialAsync(company, day4, CompanyFinancialSnapshotMoment.Seed);

        await ProcessAsync(day5, interval: 1);

        var evidence = await context.CompanyAuditEvidence.AsNoTracking().SingleAsync();
        Assert.Equal((4, 4, 5), (
            evidence.EvaluationStartTradingDayNumber,
            evidence.EvaluationEndTradingDayNumber,
            evidence.EffectiveTradingDayNumber));
    }

    [Fact]
    public async Task AllDueLiveCompaniesAreAssignedDeterministicallyAndBalanced()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var first = await AddCompanyAsync(day1, "A");
        var second = await AddCompanyAsync(day1, "B");
        var third = await AddCompanyAsync(day1, "C");
        var closed = await AddCompanyAsync(day1, "Closed");
        closed.ClosedInCycleId = day2.Id;
        var notDue = await AddCompanyAsync(day2, "New listing");
        var auditor1 = await AddAuditorAsync("First");
        var auditor2 = await AddAuditorAsync("Second");

        foreach (var company in new[] { first, second, third, closed, notDue })
        {
            await AddPriceAsync(company, 100m, day1);
            await AddPriceAsync(company, 100m, day2);
            await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        }
        await context.SaveChangesAsync();

        await ProcessAsync(day3);

        var ratings = await context.CompanyRatings
            .AsNoTracking()
            .OrderBy(rating => rating.CompanyId)
            .ToListAsync();
        Assert.Equal([first.Id, second.Id, third.Id], ratings.Select(rating => rating.CompanyId));
        Assert.Equal(
            [auditor1.Id, auditor2.Id, auditor1.Id],
            ratings.Select(rating => rating.AuditorId));
    }

    [Fact]
    public async Task NewListingUsesItsOwnListingDayCadence()
    {
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var day4 = await AddCycleAsync(4, 1);
        var company = await AddCompanyAsync(day2);
        await AddPriceAsync(company, 100m, day2);
        await AddPriceAsync(company, 102m, day3);
        await AddFinancialAsync(company, day3, CompanyFinancialSnapshotMoment.Midday);

        await ProcessAsync(day3);
        Assert.Empty(await context.CompanyRatings.AsNoTracking().ToListAsync());

        await ProcessAsync(day4);
        var evidence = await context.CompanyAuditEvidence.AsNoTracking().SingleAsync();
        Assert.Equal((2, 3, 4), (
            evidence.EvaluationStartTradingDayNumber,
            evidence.EvaluationEndTradingDayNumber,
            evidence.EffectiveTradingDayNumber));
    }

    [Fact]
    public async Task AuditDoesNotMutatePricesOrdersCashOrCreateMarketImpact()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 70m, day2);
        await AddFinancialAsync(
            company,
            day2,
            CompanyFinancialSnapshotMoment.Midday,
            profitability: CompanyMetricLevel.Low,
            volatility: CompanyMetricLevel.High,
            closureRisk: CompanyMetricLevel.High,
            outlook: ManagementOutlook.Negative,
            confidence: 100m,
            operatingCashFlow: -100m);
        var trader = await AddTraderAsync();
        var order = await AddOrderAsync(trader, company, day2);
        var priceCount = await context.PriceSnapshots.CountAsync();
        var issuerCash = company.CashBalance;

        await ProcessAsync(day3);

        Assert.Equal(priceCount, await context.PriceSnapshots.CountAsync());
        Assert.Equal(0, await context.NewsPosts.CountAsync());
        Assert.Equal(0, await context.CrisisEvents.CountAsync());
        Assert.Equal(issuerCash, (await context.Companies.AsNoTracking().SingleAsync()).CashBalance);
        var unchangedOrder = await context.Orders.AsNoTracking().SingleAsync(row => row.Id == order.Id);
        Assert.Equal(OrderStatus.Open, unchangedOrder.Status);
        Assert.Equal(500m, unchangedOrder.ReservedCashAmount);
        Assert.Null((await context.CompanyRatings.AsNoTracking().SingleAsync()).ImpactPercent);
        Assert.DoesNotContain(
            typeof(Random),
            typeof(AuditorService).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public async Task AdjustedReturnNeutralizesSplitAndReverseSplitBoundaries()
    {
        var day1Start = await AddCycleAsync(1, 1, 1);
        var day1Rise = await AddCycleAsync(1, 2, 2);
        var day1Split = await AddCycleAsync(1, 3, 3);
        var day2Merge = await AddCycleAsync(2, 1, 4);
        var day2End = await AddCycleAsync(2, 2, 5);
        var day3 = await AddCycleAsync(3, 1, 6);
        var company = await AddCompanyAsync(day1Start, issuedShares: 1000);
        await AddPriceAsync(company, 100m, day1Start, capitalization: 100_000m);
        await AddPriceAsync(company, 120m, day1Rise, capitalization: 120_000m);
        await AddDenominationAsync(
            company,
            day1Split,
            StockDenominationActionType.Split,
            ratio: 4,
            sharesBefore: 1000,
            sharesAfter: 4000,
            priceBefore: 120m,
            priceAfter: 30m);
        await AddPriceAsync(company, 30m, day1Split, capitalization: 120_000m);
        await AddDenominationAsync(
            company,
            day2Merge,
            StockDenominationActionType.ReverseSplit,
            ratio: 4,
            sharesBefore: 4000,
            sharesAfter: 1000,
            priceBefore: 30m,
            priceAfter: 120m);
        await AddPriceAsync(company, 120m, day2Merge, capitalization: 120_000m);
        await AddPriceAsync(company, 132m, day2End, capitalization: 132_000m);
        await AddFinancialAsync(company, day2End, CompanyFinancialSnapshotMoment.Midday);

        await ProcessAsync(day3);

        var evidence = await context.CompanyAuditEvidence.AsNoTracking().SingleAsync();
        Assert.Equal(32m, evidence.AdjustedReturnPercent);
        Assert.Equal(20m, evidence.MaximumAdjustedCycleMovePercent);
        Assert.Equal(1, evidence.StockSplitCount);
        Assert.Equal(1, evidence.ReverseSplitCount);
    }

    [Fact]
    public async Task FreeEmissionUsesOpeningSupplyAndExcludesOtherIssuance()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1, issuedShares: 1150);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 100m, day2, capitalization: 115_000m);
        context.ShareEmissions.Add(new ShareEmission
        {
            CompanyId = company.Id,
            SharesEmitted = 50,
            RecipientCount = 1,
            CreatedInCycleId = day2.Id,
            CreatedAt = DateTime.UtcNow,
        });
        var investor = await AddTraderAsync();
        context.CompanyInvestments.Add(new CompanyInvestment
        {
            CompanyId = company.Id,
            InvestorParticipantId = investor.Id,
            DealValue = 10_000m,
            SharesIssued = 100,
            SharesBeforeDeal = 1050,
            CapitalizationBeforeDeal = 105_000m,
            FinalCapitalization = 115_000m,
            InvestorSharePercent = 10m,
            TradingDayNumber = 2,
            CreatedInCycleId = day2.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        await context.SaveChangesAsync();

        await ProcessAsync(day3);

        var evidence = await context.CompanyAuditEvidence.AsNoTracking().SingleAsync();
        Assert.Equal(1000, evidence.OpeningIssuedShares);
        Assert.Equal(50, evidence.EmittedShares);
        Assert.Equal(5m, evidence.FreeShareDilutionPercent);
    }

    [Fact]
    public async Task AuditUsesLatestCompletedWindowFinancialsAndActualDividend()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2Opening = await AddCycleAsync(2, 1);
        var day2Midday = await AddCycleAsync(2, 2);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 100m, day2Midday);
        await AddFinancialAsync(
            company,
            day2Opening,
            CompanyFinancialSnapshotMoment.DayOpening,
            expectedPool: 80m,
            coverage: 1.5m);
        var completed = await AddFinancialAsync(
            company,
            day2Midday,
            CompanyFinancialSnapshotMoment.Midday,
            expectedPool: 200m,
            coverage: 0.75m,
            profitability: CompanyMetricLevel.High,
            volatility: CompanyMetricLevel.Low,
            closureRisk: CompanyMetricLevel.Low,
            outlook: ManagementOutlook.Positive,
            confidence: 80m,
            operatingCashFlow: 500m);
        var dividend = await AddDividendAsync(company, day2Midday, 2, DividendFundingOutcome.Reduced);
        await AddFinancialAsync(
            company,
            day3,
            CompanyFinancialSnapshotMoment.DayOpening,
            expectedPool: 500m,
            coverage: 4m,
            profitability: CompanyMetricLevel.Low,
            volatility: CompanyMetricLevel.High,
            closureRisk: CompanyMetricLevel.High,
            outlook: ManagementOutlook.Negative,
            confidence: 100m,
            operatingCashFlow: -500m);
        await AddDividendAsync(company, day3, 3, DividendFundingOutcome.Paid);

        await ProcessAsync(day3);

        var evidence = await context.CompanyAuditEvidence.AsNoTracking().SingleAsync();
        Assert.Equal(completed.Id, evidence.CompanyFinancialSnapshotId);
        Assert.Equal(dividend.Id, evidence.LatestDividendEventId);
        Assert.Equal(200m, evidence.ModeledMaximumDividend);
        Assert.Equal(0.75m, evidence.DividendCoverageRatio);
        Assert.Equal(2, evidence.ProfitabilityFactorScore);
        Assert.Equal(1, evidence.StabilityFactorScore);
        Assert.Equal(2, evidence.ClosureRiskFactorScore);
        Assert.Equal(2, evidence.ManagementOutlookFactorScore);
    }

    [Fact]
    public async Task LatestDividendBeforeLaterWindowRemainsEvidence()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var day4 = await AddCycleAsync(4, 1);
        var day5 = await AddCycleAsync(5, 1);
        var company = await AddCompanyAsync(day1);
        foreach (var cycle in new[] { day1, day2, day3, day4 })
        {
            await AddPriceAsync(company, 100m, cycle);
        }
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        await AddFinancialAsync(company, day4, CompanyFinancialSnapshotMoment.Midday);
        var dividend = await AddDividendAsync(company, day2, 2, DividendFundingOutcome.Paid);

        await ProcessAsync(day3);
        await ProcessAsync(day5);

        var latest = await context.CompanyAuditEvidence
            .AsNoTracking()
            .OrderByDescending(evidence => evidence.EffectiveTradingDayNumber)
            .FirstAsync();
        Assert.Equal((3, 4), (
            latest.EvaluationStartTradingDayNumber,
            latest.EvaluationEndTradingDayNumber));
        Assert.Equal(dividend.Id, latest.LatestDividendEventId);
    }

    [Fact]
    public async Task ArchivedAndLiveHistoryProducePriceAndIndustryEvidence()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        await AddArchivedPriceAsync(company, 100m, day1, capitalization: 100_000m, id: 10);
        await AddPriceAsync(company, 105m, day2, capitalization: 105_000m, id: 20);
        await AddArchivedPriceAsync(company, 999m, day2, capitalization: 999_000m, id: 20);
        context.SectorSentimentSnapshotArchives.Add(new SectorSentimentSnapshotArchive
        {
            IndustryId = industry.Id,
            SentimentValue = 100,
            CreatedInCycleId = day1.Id,
            CreatedAt = DateTime.UtcNow,
        });
        context.SectorSentimentSnapshots.Add(new SectorSentimentSnapshot
        {
            IndustryId = industry.Id,
            SentimentValue = 115,
            CreatedInCycleId = day2.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        await context.SaveChangesAsync();

        await ProcessAsync(day3);

        var evidence = await context.CompanyAuditEvidence.AsNoTracking().SingleAsync();
        Assert.Equal(100m, evidence.StartPrice);
        Assert.Equal(105m, evidence.EndPrice);
        Assert.Equal(5m, evidence.AdjustedReturnPercent);
        Assert.Equal(100, evidence.OpeningIndustrySentiment);
        Assert.Equal(115, evidence.ClosingIndustrySentiment);
        Assert.Equal(IndustryTrend.Rising, evidence.IndustryTrend);
        Assert.Equal(1, evidence.IndustryScore);
    }

    [Fact]
    public async Task LaterAuditCarriesThePriorPriceAcrossAQuietWindow()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var day4 = await AddCycleAsync(4, 1);
        var day5 = await AddCycleAsync(5, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 95m, day1);
        await AddPriceAsync(company, 100m, day2);
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        await AddFinancialAsync(company, day4, CompanyFinancialSnapshotMoment.Midday);

        await ProcessAsync(day3);
        await ProcessAsync(day5);

        var evidence = await context.CompanyAuditEvidence
            .AsNoTracking()
            .OrderByDescending(row => row.EffectiveTradingDayNumber)
            .FirstAsync();
        Assert.Equal(100m, evidence.StartPrice);
        Assert.Equal(100m, evidence.EndPrice);
        Assert.Equal(0m, evidence.AdjustedReturnPercent);
        Assert.Equal(0m, evidence.MaximumAdjustedCycleMovePercent);
    }

    [Fact]
    public async Task CarryInOrdersByTimeAndAppliesOnlyDenominationsBetweenSelectedPrices()
    {
        var timestamp = DateTime.UtcNow;
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var day4 = await AddCycleAsync(4, 1);
        var day5 = await AddCycleAsync(5, 1);
        var company = await AddCompanyAsync(day1, issuedShares: 4000);
        await AddPriceAsync(company, 100m, day2, createdAt: timestamp, id: 20);
        await AddDenominationAsync(
            company,
            day1,
            StockDenominationActionType.Split,
            ratio: 2,
            sharesBefore: 500,
            sharesAfter: 1000,
            priceBefore: 200m,
            priceAfter: 100m);
        await AddDenominationAsync(
            company,
            day3,
            StockDenominationActionType.Split,
            ratio: 4,
            sharesBefore: 1000,
            sharesAfter: 4000,
            priceBefore: 100m,
            priceAfter: 25m);
        await AddPriceAsync(
            company,
            27.5m,
            day4,
            createdAt: timestamp.AddMinutes(1),
            id: 50);
        await AddPriceAsync(
            company,
            30m,
            day4,
            createdAt: timestamp.AddMinutes(2),
            id: 40);
        await AddDenominationAsync(
            company,
            day5,
            StockDenominationActionType.ReverseSplit,
            ratio: 4,
            sharesBefore: 4000,
            sharesAfter: 1000,
            priceBefore: 30m,
            priceAfter: 120m);
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        await AddFinancialAsync(company, day4, CompanyFinancialSnapshotMoment.Midday);

        await ProcessAsync(day3);
        await ProcessAsync(day5);

        var evidence = await context.CompanyAuditEvidence
            .AsNoTracking()
            .OrderByDescending(row => row.EffectiveTradingDayNumber)
            .FirstAsync();
        Assert.Equal(100m, evidence.StartPrice);
        Assert.Equal(30m, evidence.EndPrice);
        Assert.Equal(20m, evidence.AdjustedReturnPercent);
        Assert.Equal(9.090909m, evidence.MaximumAdjustedCycleMovePercent);
        Assert.Equal(1, evidence.StockSplitCount);
        Assert.Equal(0, evidence.ReverseSplitCount);
    }

    [Fact]
    public async Task OpeningSupplyAndIssuerCashReverseCompletedAndEffectiveDayActions()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1, issuedShares: 5150);
        company.CashBalance = 800m;
        await context.SaveChangesAsync();
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 100m, day2);
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        var investor = await AddTraderAsync();

        await AddEmissionAsync(company, day1, 50);
        await AddPrimaryIssuanceAsync(company, day1, 1050, 100, 1150);
        await AddInvestmentAsync(company, investor, day1, 50, 1150);

        await AddDenominationAsync(
            company,
            day3,
            StockDenominationActionType.Split,
            ratio: 4,
            sharesBefore: 1200,
            sharesAfter: 4800,
            priceBefore: 100m,
            priceAfter: 25m);
        await AddEmissionAsync(company, day3, 200);
        await AddPrimaryIssuanceAsync(company, day3, 5000, 100, 5100);
        await AddInvestmentAsync(company, investor, day3, 50, 5100);

        AddCorporateCash(company, day3, CorporateCashTransactionType.OperatingIncome, 100m);
        AddCorporateCash(company, day3, CorporateCashTransactionType.BigInvestment, 200m);
        AddCorporateCash(company, day3, CorporateCashTransactionType.PrimaryIssuance, 50m);
        AddCorporateCash(company, day3, CorporateCashTransactionType.DividendDeclared, 30m);
        AddCorporateCash(company, day3, CorporateCashTransactionType.ClosureDistribution, 20m);
        await context.SaveChangesAsync();

        await ProcessAsync(day3);

        var evidence = await context.CompanyAuditEvidence.AsNoTracking().SingleAsync();
        Assert.Equal(1000, evidence.OpeningIssuedShares);
        Assert.Equal(50, evidence.EmittedShares);
        Assert.Equal(5m, evidence.FreeShareDilutionPercent);
        Assert.Equal(500m, evidence.IssuerCash);
    }

    [Fact]
    public async Task UnknownCorporateCashMovementFailsInsteadOfGuessingItsDirection()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 100m, day2);
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        AddCorporateCash(company, day3, (CorporateCashTransactionType)999, 10m);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Service().ProcessForCycleAsync(day3.Id, day3.CycleNumber, DateTime.UtcNow));
    }

    [Fact]
    public async Task NegativeReconstructedIssuerCashIsRejected()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        company.CashBalance = 10m;
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 100m, day2);
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        AddCorporateCash(company, day3, CorporateCashTransactionType.OperatingIncome, 100m);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().ProcessForCycleAsync(day3.Id, day3.CycleNumber, DateTime.UtcNow));
        Assert.Contains("negative", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StagedCurrentCycleCashRowsRemainAvailableToAuditReconstruction()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        company.CashBalance = 600m;
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 100m, day2);
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        await AddAuditorAsync("Existing auditor");
        AddCorporateCash(
            company,
            day3,
            CorporateCashTransactionType.OperatingIncome,
            100m);

        await Service().ProcessForCycleAsync(
            day3.Id,
            day3.CycleNumber,
            DateTime.UtcNow);

        var evidence = Assert.Single(
            context.ChangeTracker.Entries<CompanyAuditEvidence>(),
            entry => entry.State == EntityState.Added).Entity;
        Assert.Equal(500m, evidence.IssuerCash);
    }

    [Fact]
    public async Task MissingFinancialSnapshotProducesConservativeEvidence()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 100m, day2);

        await ProcessAsync(day3);

        var evidence = await context.CompanyAuditEvidence.AsNoTracking().SingleAsync();
        var rating = await context.CompanyRatings.AsNoTracking().SingleAsync();
        Assert.Null(evidence.CompanyFinancialSnapshotId);
        Assert.Equal(0, evidence.DividendCoverageScore);
        Assert.Equal(0, evidence.ProfitabilityFactorScore);
        Assert.Equal(0, evidence.StabilityFactorScore);
        Assert.Equal(0, evidence.ClosureRiskFactorScore);
        Assert.Equal(0, evidence.ManagementOutlookFactorScore);
        Assert.Equal(CompanyRiskRating.LowRisk, rating.Rating);
    }

    [Fact]
    public async Task MissingAuditorsAreCreatedDeterministicallyWithoutRandomDependency()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 100m, day2);
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);

        await ProcessAsync(day3);

        Assert.Single(await context.Auditors.AsNoTracking().ToListAsync());
        Assert.Equal(
            (await context.Auditors.AsNoTracking().SingleAsync()).Id,
            (await context.CompanyRatings.AsNoTracking().SingleAsync()).AuditorId);
    }

    [Fact]
    public async Task EvidenceTablesAreReadOnceForTheWholeDueBatch()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        for (var index = 0; index < 3; index++)
        {
            var company = await AddCompanyAsync(day1);
            await AddPriceAsync(company, 100m + index, day1);
            await AddPriceAsync(company, 101m + index, day2);
            await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);
        }

        commands.Clear();
        await ProcessAsync(day3);

        Assert.Equal(3, await context.CompanyRatings.CountAsync());
        foreach (var table in new[]
        {
            "PriceSnapshots",
            "PriceSnapshotArchives",
            "StockDenominationEvents",
            "ShareEmissions",
            "PrimaryIssuanceEvents",
            "CompanyInvestments",
            "CorporateCashTransactions",
            "CompanyDividendEvents",
            "CompanyFinancialSnapshots",
            "SectorSentimentSnapshots",
            "SectorSentimentSnapshotArchives",
        })
        {
            Assert.Equal(1, commands.ReaderCount(table));
        }
    }

    [Fact]
    public async Task EvidenceHistoryIsBoundedByRunCycleAndDayInSql()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 101m, day2);
        await AddFinancialAsync(company, day2, CompanyFinancialSnapshotMoment.Midday);

        commands.Clear();
        await ProcessAsync(day3);

        foreach (var table in new[]
        {
            "PriceSnapshots",
            "PriceSnapshotArchives",
            "StockDenominationEvents",
            "ShareEmissions",
            "PrimaryIssuanceEvents",
            "CompanyInvestments",
            "CorporateCashTransactions",
            "CompanyDividendEvents",
            "CompanyFinancialSnapshots",
            "SectorSentimentSnapshots",
            "SectorSentimentSnapshotArchives",
        })
        {
            var sql = commands.SingleReaderCommand(table);
            Assert.Contains("MarketCycles", sql, StringComparison.Ordinal);
            Assert.Contains("MarketRunId", sql, StringComparison.Ordinal);
        }

        foreach (var table in new[]
        {
            "PriceSnapshots",
            "PriceSnapshotArchives",
            "StockDenominationEvents",
            "ShareEmissions",
            "PrimaryIssuanceEvents",
            "CompanyInvestments",
            "CorporateCashTransactions",
            "SectorSentimentSnapshots",
            "SectorSentimentSnapshotArchives",
        })
        {
            Assert.Contains(
                "TradingDays",
                commands.SingleReaderCommand(table),
                StringComparison.Ordinal);
        }

        Assert.Contains(
            commands.ReaderCommands("MarketCycles"),
            sql => sql.Contains("MarketRunId", StringComparison.Ordinal)
                && sql.Contains("\"DayNumber\" >= ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvidenceQueryCountDoesNotGrowWithDueCompanyCount()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var firstCompany = await AddCompanyAsync(day1);
        await AddPriceAsync(firstCompany, 100m, day1);
        await AddPriceAsync(firstCompany, 101m, day2);
        await AddFinancialAsync(firstCompany, day2, CompanyFinancialSnapshotMoment.Midday);

        commands.Clear();
        await ProcessAsync(day3);
        var singleCompanyCounts = EvidenceReaderCounts();

        var day4 = await AddCycleAsync(4, 1);
        var day5 = await AddCycleAsync(5, 1);
        await AddPriceAsync(firstCompany, 102m, day4);
        await AddFinancialAsync(firstCompany, day4, CompanyFinancialSnapshotMoment.Midday);
        for (var index = 0; index < 2; index++)
        {
            var company = await AddCompanyAsync(day3);
            await AddPriceAsync(company, 100m + index, day3);
            await AddPriceAsync(company, 101m + index, day4);
            await AddFinancialAsync(company, day4, CompanyFinancialSnapshotMoment.Midday);
        }

        commands.Clear();
        await ProcessAsync(day5);

        Assert.Equal(3, await context.CompanyRatings
            .CountAsync(rating => rating.CreatedInCycleId == day5.Id));
        Assert.Equal(singleCompanyCounts, EvidenceReaderCounts());
    }

    [Fact]
    public async Task EvidenceRowsOutsideCurrentRunAndCycleBoundsAreIgnored()
    {
        var day1 = await AddCycleAsync(1, 1);
        var day2 = await AddCycleAsync(2, 1);
        var day3 = await AddCycleAsync(3, 1);
        var company = await AddCompanyAsync(day1);
        await AddPriceAsync(company, 100m, day1);
        await AddPriceAsync(company, 110m, day2);
        await AddFinancialAsync(
            company,
            day2,
            CompanyFinancialSnapshotMoment.DayOpening,
            expectedPool: 100m,
            coverage: 2m);

        var foreignRunCycle = await AddCycleAsync(2, 2);
        foreignRunCycle.MarketRunId = 99;
        await context.SaveChangesAsync();
        await AddPriceAsync(company, 1000m, foreignRunCycle);
        await AddFinancialAsync(
            company,
            foreignRunCycle,
            CompanyFinancialSnapshotMoment.Midday,
            expectedPool: 900m,
            coverage: 0.1m);

        var futureCycle = await AddCycleAsync(4, 1);
        await AddPriceAsync(company, 2000m, futureCycle);
        await AddFinancialAsync(
            company,
            futureCycle,
            CompanyFinancialSnapshotMoment.Midday,
            expectedPool: 800m,
            coverage: 0.2m);

        await ProcessAsync(day3);

        var evidence = await context.CompanyAuditEvidence.AsNoTracking().SingleAsync();
        Assert.Equal(110m, evidence.EndPrice);
        Assert.Equal(100m, evidence.ModeledMaximumDividend);
        Assert.Equal(2m, evidence.DividendCoverageRatio);
    }

    private int[] EvidenceReaderCounts() =>
        new[]
        {
            "PriceSnapshots",
            "PriceSnapshotArchives",
            "StockDenominationEvents",
            "ShareEmissions",
            "PrimaryIssuanceEvents",
            "CompanyInvestments",
            "CorporateCashTransactions",
            "CompanyDividendEvents",
            "CompanyFinancialSnapshots",
            "SectorSentimentSnapshots",
            "SectorSentimentSnapshotArchives",
        }
        .Select(commands.ReaderCount)
        .ToArray();

    private AuditorService Service(bool enabled = true, int interval = 2) =>
        new(context, Options.Create(new AuditorOptions
        {
            Enabled = enabled,
            AuditIntervalTradingDays = interval,
        }));

    private async Task ProcessAsync(MarketCycle cycle, int interval = 2)
    {
        await Service(interval: interval).ProcessForCycleAsync(
            cycle.Id,
            cycle.CycleNumber,
            DateTime.UtcNow);
        await context.SaveChangesAsync();
    }

    private async Task<MarketCycle> AddCycleAsync(
        int dayNumber,
        int tradingCycleNumber,
        int? cycleNumber = null)
    {
        if (!days.TryGetValue(dayNumber, out var day))
        {
            day = new TradingDay
            {
                DayNumber = dayNumber,
                State = TradingSessionState.Trading,
                OpenedInCycleId = 0,
            };
            days.Add(dayNumber, day);
            context.TradingDays.Add(day);
            await context.SaveChangesAsync();
        }

        var cycle = new MarketCycle
        {
            CycleNumber = cycleNumber ?? dayNumber * 100 + tradingCycleNumber,
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
        MarketCycle listingCycle,
        string? name = null,
        int issuedShares = 1000)
    {
        var company = new Company
        {
            Name = name ?? $"Company {Guid.NewGuid():N}",
            IndustryId = industry.Id,
            IssuedSharesCount = issuedShares,
            CashBalance = 1000m,
            CreatedInCycleId = listingCycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        return company;
    }

    private async Task<Auditor> AddAuditorAsync(string name)
    {
        var auditor = new Auditor
        {
            Name = name,
            Description = "Test auditor",
            CreatedAt = DateTime.UtcNow,
        };
        context.Auditors.Add(auditor);
        await context.SaveChangesAsync();
        return auditor;
    }

    private async Task AddPriceAsync(
        Company company,
        decimal price,
        MarketCycle cycle,
        decimal? capitalization = null,
        DateTime? createdAt = null,
        int? id = null)
    {
        var snapshot = new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = price,
            Capitalization = capitalization,
            CreatedInCycleId = cycle.Id,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        if (id is int snapshotId)
        {
            snapshot.Id = snapshotId;
        }
        context.PriceSnapshots.Add(snapshot);
        await context.SaveChangesAsync();
    }

    private async Task AddArchivedPriceAsync(
        Company company,
        decimal price,
        MarketCycle cycle,
        decimal? capitalization = null,
        int? id = null)
    {
        var snapshot = new PriceSnapshotArchive
        {
            CompanyId = company.Id,
            Price = price,
            Capitalization = capitalization,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        };
        if (id is int snapshotId)
        {
            snapshot.Id = snapshotId;
        }
        context.PriceSnapshotArchives.Add(snapshot);
        await context.SaveChangesAsync();
    }

    private async Task<CompanyFinancialSnapshot> AddFinancialAsync(
        Company company,
        MarketCycle cycle,
        CompanyFinancialSnapshotMoment moment,
        decimal expectedPool = 100m,
        decimal coverage = 2m,
        CompanyMetricLevel profitability = CompanyMetricLevel.Medium,
        CompanyMetricLevel volatility = CompanyMetricLevel.Medium,
        CompanyMetricLevel closureRisk = CompanyMetricLevel.Medium,
        ManagementOutlook outlook = ManagementOutlook.Neutral,
        decimal confidence = 50m,
        decimal operatingCashFlow = 100m)
    {
        var dayNumber = days.Values.Single(day => day.Id == cycle.TradingDayId).DayNumber;
        var snapshot = new CompanyFinancialSnapshot
        {
            CompanyId = company.Id,
            CreatedInCycleId = cycle.Id,
            TradingDayNumber = dayNumber,
            Moment = moment,
            CreatedAt = DateTime.UtcNow,
            Revenue = 1000m,
            NetProfit = 100m,
            OperatingCashFlow = operatingCashFlow,
            TotalAssets = 2000m,
            TotalLiabilities = 500m,
            TotalDebt = 250m,
            ExpectedDividendPerShare = expectedPool / company.IssuedSharesCount,
            ExpectedDividendPool = expectedPool,
            DividendCoverageRatio = coverage,
            BusinessRiskScore = 50m,
            ManagementRevenueForecast = 1000m,
            ManagementProfitForecast = 100m,
            ManagementOperatingCashFlowForecast = operatingCashFlow,
            ManagementOutlook = outlook,
            ManagementConfidenceScore = confidence,
            ProfitabilityScore = 50m,
            ProfitabilityLevel = profitability,
            StabilityScore = 50m,
            FinancialVolatilityLevel = volatility,
            ClosureRiskScore = 50m,
            ClosureRiskLevel = closureRisk,
        };
        context.CompanyFinancialSnapshots.Add(snapshot);
        await context.SaveChangesAsync();
        return snapshot;
    }

    private async Task<CompanyDividendEvent> AddDividendAsync(
        Company company,
        MarketCycle cycle,
        int tradingDayNumber,
        DividendFundingOutcome outcome)
    {
        var dividend = new CompanyDividendEvent
        {
            CompanyId = company.Id,
            DeclaredAmount = 100m,
            FundedAmount = outcome switch
            {
                DividendFundingOutcome.Paid => 100m,
                DividendFundingOutcome.Reduced => 50m,
                DividendFundingOutcome.Skipped => 0m,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            },
            FundingOutcome = outcome,
            IssuerCashBeforeFunding = 50m,
            CreatedInCycleId = cycle.Id,
            TradingDayNumber = tradingDayNumber,
            CreatedAt = DateTime.UtcNow,
        };
        context.CompanyDividendEvents.Add(dividend);
        await context.SaveChangesAsync();
        return dividend;
    }

    private async Task AddDenominationAsync(
        Company company,
        MarketCycle cycle,
        StockDenominationActionType action,
        int ratio,
        int sharesBefore,
        int sharesAfter,
        decimal priceBefore,
        decimal priceAfter)
    {
        context.StockDenominationEvents.Add(new StockDenominationEvent
        {
            CompanyId = company.Id,
            ActionType = action,
            Ratio = ratio,
            IssuedSharesBefore = sharesBefore,
            IssuedSharesAfter = sharesAfter,
            PriceBefore = priceBefore,
            PriceAfter = priceAfter,
            EffectiveInCycleId = cycle.Id,
            EffectiveInCycleNumber = cycle.CycleNumber,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task AddEmissionAsync(
        Company company,
        MarketCycle cycle,
        int shares)
    {
        context.ShareEmissions.Add(new ShareEmission
        {
            CompanyId = company.Id,
            SharesEmitted = shares,
            RecipientCount = 1,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task AddPrimaryIssuanceAsync(
        Company company,
        MarketCycle cycle,
        int before,
        int newlyIssued,
        int after)
    {
        context.PrimaryIssuanceEvents.Add(new PrimaryIssuanceEvent
        {
            CompanyId = company.Id,
            CreatedInCycleId = cycle.Id,
            IssuedSharesBefore = before,
            NewlyIssuedShares = newlyIssued,
            IssuedSharesAfter = after,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task AddInvestmentAsync(
        Company company,
        Participant investor,
        MarketCycle cycle,
        int issuedShares,
        int sharesBefore)
    {
        context.CompanyInvestments.Add(new CompanyInvestment
        {
            CompanyId = company.Id,
            InvestorParticipantId = investor.Id,
            DealValue = 100m,
            SharesIssued = issuedShares,
            SharesBeforeDeal = sharesBefore,
            CapitalizationBeforeDeal = sharesBefore * 100m,
            FinalCapitalization = (sharesBefore + issuedShares) * 100m,
            InvestorSharePercent = 1m,
            TradingDayNumber = days.Values.Single(day => day.Id == cycle.TradingDayId).DayNumber,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private void AddCorporateCash(
        Company company,
        MarketCycle cycle,
        CorporateCashTransactionType type,
        decimal amount)
    {
        context.CorporateCashTransactions.Add(new CorporateCashTransaction
        {
            CompanyId = company.Id,
            Type = type,
            Amount = amount,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
    }

    private Task<Participant> AddTraderAsync() =>
        AddParticipantAsync(ParticipantType.Individual);

    private async Task<Participant> AddParticipantAsync(ParticipantType type)
    {
        var trader = new Participant
        {
            Name = $"Trader {Guid.NewGuid():N}",
            Type = type,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 10_000m,
            CurrentBalance = 10_000m,
            SettledCashBalance = 10_000m,
            ReservedBalance = 500m,
            IsActive = true,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
    }

    private async Task<CollectiveFund> AddFundAsync(
        Participant fundParticipant,
        Participant founder,
        bool isPlayerManaged,
        MarketCycle cycle)
    {
        var fund = new CollectiveFund
        {
            ParticipantId = fundParticipant.Id,
            FoundedByParticipantId = founder.Id,
            IsPlayerManaged = isPlayerManaged,
            Status = CollectiveFundStatus.Active,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        };
        context.CollectiveFunds.Add(fund);
        await context.SaveChangesAsync();
        return fund;
    }

    private async Task<Holding> AddHoldingAsync(
        Participant participant,
        Company company,
        int quantity)
    {
        var holding = new Holding
        {
            ParticipantId = participant.Id,
            CompanyId = company.Id,
            Quantity = quantity,
            SettledQuantity = quantity,
            AverageCost = 100m,
        };
        context.Holdings.Add(holding);
        await context.SaveChangesAsync();
        return holding;
    }

    private async Task<Order> AddOrderAsync(
        Participant trader,
        Company company,
        MarketCycle cycle)
    {
        var order = new Order
        {
            ParticipantId = trader.Id,
            CompanyId = company.Id,
            Type = OrderType.Buy,
            Status = OrderStatus.Open,
            Quantity = 5,
            LimitPrice = 100m,
            ReservedCashAmount = 500m,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order;
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> commands = [];

        public void Clear() => commands.Clear();

        public int ReaderCount(string table) => commands.Count(command =>
            command.Contains($"FROM \"{table}\"", StringComparison.Ordinal));

        public IReadOnlyList<string> ReaderCommands(string table) => commands
            .Where(command => command.Contains($"FROM \"{table}\"", StringComparison.Ordinal))
            .ToArray();

        public string SingleReaderCommand(string table) => Assert.Single(
            commands,
            command => command.Contains($"FROM \"{table}\"", StringComparison.Ordinal));

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
