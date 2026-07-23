using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AuditorServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly Dictionary<int, TradingDay> days = [];
    private readonly Industry industry;

    public AuditorServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
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
        await AddArchivedPriceAsync(company, 100m, day1, capitalization: 100_000m);
        await AddPriceAsync(company, 105m, day2, capitalization: 105_000m);
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
        decimal? capitalization = null)
    {
        context.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = price,
            Capitalization = capitalization,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task AddArchivedPriceAsync(
        Company company,
        decimal price,
        MarketCycle cycle,
        decimal? capitalization = null)
    {
        context.PriceSnapshotArchives.Add(new PriceSnapshotArchive
        {
            CompanyId = company.Id,
            Price = price,
            Capitalization = capitalization,
            CreatedInCycleId = cycle.Id,
            CreatedAt = DateTime.UtcNow,
        });
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

    private async Task<Participant> AddTraderAsync()
    {
        var trader = new Participant
        {
            Name = $"Trader {Guid.NewGuid():N}",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = RiskProfile.Medium,
            InitialBalance = 10_000m,
            CurrentBalance = 10_000m,
            ReservedBalance = 500m,
            IsActive = true,
        };
        context.Participants.Add(trader);
        await context.SaveChangesAsync();
        return trader;
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
}
