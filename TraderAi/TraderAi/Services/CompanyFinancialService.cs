using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed class CompanyFinancialService
{
    private const int MoneyPrecision = 2;
    private const int RatioPrecision = 6;
    private const decimal MinimumMoneyChangeBase = 1m;
    private const decimal MinimumPerShareChangeBase = 0.000001m;

    private readonly AppDbContext dbContext;
    private readonly CompanyFinancialOptions financialOptions;
    private readonly RandomChanceRatesOptions chanceRates;
    private readonly TradingClockOptions clockOptions;
    private readonly Random random;
    private readonly CompanyFinancialScorer scorer;

    public CompanyFinancialService(
        AppDbContext dbContext,
        IOptions<CompanyFinancialOptions> financialOptions,
        IOptions<RandomChanceRatesOptions> chanceRates,
        IOptions<TradingClockOptions> clockOptions,
        Random random,
        CompanyFinancialScorer scorer)
    {
        this.dbContext = dbContext;
        this.financialOptions = financialOptions.Value;
        this.chanceRates = chanceRates.Value;
        this.clockOptions = clockOptions.Value;
        this.random = random;
        this.scorer = scorer;
    }

    public async Task<CompanyFinancialSnapshot?> StageSeedSnapshotAsync(
        Company company,
        decimal listingPrice,
        int currentCycleId,
        int tradingDayNumber,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (!financialOptions.Enabled)
        {
            return null;
        }

        if (company.Id <= 0)
        {
            throw new ArgumentException(
                "The company must be persisted before its financial state is seeded.",
                nameof(company));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(listingPrice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentCycleId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tradingDayNumber);

        var existing = LocalSnapshot(
            company.Id,
            tradingDayNumber,
            CompanyFinancialSnapshotMoment.Seed)
            ?? await dbContext.CompanyFinancialSnapshots
                .AsNoTracking()
                .SingleOrDefaultAsync(snapshot =>
                    snapshot.CompanyId == company.Id
                    && snapshot.TradingDayNumber == tradingDayNumber
                    && snapshot.Moment == CompanyFinancialSnapshotMoment.Seed);
        if (existing is not null)
        {
            return existing;
        }

        var magnitudes = chanceRates.RandomMagnitudeBands;
        var capitalization = Money(listingPrice * company.IssuedSharesCount);
        var assets = Money(capitalization * DrawRange(
            magnitudes.FinancialSeedAssetsToMarketCapMin,
            magnitudes.FinancialSeedAssetsToMarketCapMax));
        var revenue = Money(assets * DrawRange(
            magnitudes.FinancialSeedRevenueToAssetsMin,
            magnitudes.FinancialSeedRevenueToAssetsMax));
        var netProfit = Money(revenue * DrawRange(
            magnitudes.FinancialSeedNetMarginMin,
            magnitudes.FinancialSeedNetMarginMax));
        var operatingCashFlow = Money(netProfit * DrawRange(
            magnitudes.FinancialSeedOperatingCashFlowToProfitMin,
            magnitudes.FinancialSeedOperatingCashFlowToProfitMax));
        var liabilities = Money(assets * DrawRange(
            magnitudes.FinancialSeedLiabilitiesToAssetsMin,
            magnitudes.FinancialSeedLiabilitiesToAssetsMax));
        var debt = Money(liabilities * DrawRange(
            magnitudes.FinancialSeedDebtToLiabilitiesMin,
            magnitudes.FinancialSeedDebtToLiabilitiesMax));
        var dividendPerShare = Ratio(listingPrice * DrawRange(
            magnitudes.FinancialSeedExpectedDividendYieldMin,
            magnitudes.FinancialSeedExpectedDividendYieldMax));
        var businessRisk = Ratio(DrawRange(
            magnitudes.FinancialSeedBusinessRiskScoreMin,
            magnitudes.FinancialSeedBusinessRiskScoreMax));
        var revenueForecast = Forecast(
            revenue,
            DrawSeedForecastDeviation(magnitudes));
        var profitForecast = Forecast(
            netProfit,
            DrawSeedForecastDeviation(magnitudes));
        var cashFlowForecast = Forecast(
            operatingCashFlow,
            DrawSeedForecastDeviation(magnitudes));
        var managementConfidence = Ratio(DrawRange(
            magnitudes.FinancialSeedManagementConfidenceMin,
            magnitudes.FinancialSeedManagementConfidenceMax));
        var dividendPool = Money(dividendPerShare * company.IssuedSharesCount);
        var state = new CompanyFinancialState(
            revenue,
            netProfit,
            operatingCashFlow,
            assets,
            liabilities,
            Math.Min(debt, liabilities),
            dividendPerShare,
            dividendPool,
            DividendCoverage(netProfit, dividendPool),
            businessRisk,
            revenueForecast,
            profitForecast,
            cashFlowForecast,
            managementConfidence);
        var trend = await IndustryTrendForAsync(company.IndustryId);
        var score = scorer.Score(new CompanyFinancialScoringInput(state, [], trend));
        var latestDividendEventId = await LatestDividendEventIdAsync(company.Id);
        var snapshot = CreateSnapshot(
            company.Id,
            currentCycleId,
            tradingDayNumber,
            CompanyFinancialSnapshotMoment.Seed,
            now,
            state,
            score,
            latestDividendEventId,
            CompanyFinancialMetric.All);

        dbContext.CompanyFinancialSnapshots.Add(snapshot);
        return snapshot;
    }

    public async Task ProcessForCycleAsync(int currentCycleId, DateTime now)
    {
        if (!financialOptions.Enabled)
        {
            return;
        }

        var checkpoint = await (
                from cycle in dbContext.MarketCycles.AsNoTracking()
                join day in dbContext.TradingDays.AsNoTracking()
                    on cycle.TradingDayId equals day.Id
                where cycle.Id == currentCycleId
                select new
                {
                    cycle.TradingCycleNumber,
                    TradingDayNumber = day.DayNumber,
                })
            .SingleOrDefaultAsync()
            ?? throw new InvalidOperationException(
                $"Market cycle {currentCycleId} does not belong to a trading day.");
        var moment = MomentFor(checkpoint.TradingCycleNumber);
        if (moment is null)
        {
            return;
        }

        var companies = await (
                from company in dbContext.Companies.AsNoTracking()
                join industry in dbContext.Industries.AsNoTracking()
                    on company.IndustryId equals industry.Id
                where company.ClosedInCycleId == null
                orderby company.Id
                select new FinancialCompany(
                    company.Id,
                    company.IssuedSharesCount,
                    industry.SentimentValue))
            .ToListAsync();
        if (companies.Count == 0)
        {
            return;
        }

        var companyIds = companies.Select(company => company.Id).ToArray();
        var existingCompanyIds = (await dbContext.CompanyFinancialSnapshots
                .AsNoTracking()
                .Where(snapshot =>
                    companyIds.Contains(snapshot.CompanyId)
                    && snapshot.TradingDayNumber == checkpoint.TradingDayNumber
                    && snapshot.Moment == moment.Value)
                .Select(snapshot => snapshot.CompanyId)
                .ToListAsync())
            .ToHashSet();
        existingCompanyIds.UnionWith(
            dbContext.ChangeTracker
                .Entries<CompanyFinancialSnapshot>()
                .Where(entry =>
                    entry.State != EntityState.Deleted
                    && entry.Entity.TradingDayNumber == checkpoint.TradingDayNumber
                    && entry.Entity.Moment == moment.Value)
                .Select(entry => entry.Entity.CompanyId));

        var eligibleCompanies = companies
            .Where(company => !existingCompanyIds.Contains(company.Id))
            .ToList();
        if (eligibleCompanies.Count == 0)
        {
            return;
        }

        var rows = await dbContext.CompanyFinancialSnapshots
            .AsNoTracking()
            .Where(snapshot => companyIds.Contains(snapshot.CompanyId))
            .OrderBy(snapshot => snapshot.TradingDayNumber)
            .ThenBy(snapshot => snapshot.Moment)
            .ThenBy(snapshot => snapshot.CreatedInCycleId)
            .ThenBy(snapshot => snapshot.CreatedAt)
            .ThenBy(snapshot => snapshot.Id)
            .ToListAsync();
        rows.AddRange(
            dbContext.ChangeTracker
                .Entries<CompanyFinancialSnapshot>()
                .Where(entry => entry.State == EntityState.Added)
                .Select(entry => entry.Entity));
        var rowsByCompany = rows
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(snapshot => snapshot.TradingDayNumber)
                    .ThenBy(snapshot => snapshot.Moment)
                    .ThenBy(snapshot => snapshot.CreatedInCycleId)
                    .ThenBy(snapshot => snapshot.CreatedAt)
                    .ThenBy(snapshot => snapshot.Id)
                    .ToArray());

        var missingCompany = eligibleCompanies.FirstOrDefault(company =>
            !rowsByCompany.TryGetValue(company.Id, out var history)
            || history.Length == 0);
        if (missingCompany is not null)
        {
            throw new InvalidOperationException(
                $"Company {missingCompany.Id} has no financial seed snapshot.");
        }

        var latestDividendIds = await LatestDividendEventIdsAsync(companyIds);

        foreach (var company in eligibleCompanies)
        {
            var history = rowsByCompany[company.Id];
            var current = MutableFinancialState.From(history[^1]);
            var changedMetrics = UpdateState(current, company.IndustryTrend);
            EnforceUpdateInvariants(current, company.IssuedSharesCount, ref changedMetrics);
            var state = current.ToImmutable();
            var score = scorer.Score(new CompanyFinancialScoringInput(
                state,
                history.Select(HistoryPoint).ToArray(),
                company.IndustryTrend));
            var snapshot = CreateSnapshot(
                company.Id,
                currentCycleId,
                checkpoint.TradingDayNumber,
                moment.Value,
                now,
                state,
                score,
                latestDividendIds.GetValueOrDefault(company.Id),
                changedMetrics);
            dbContext.CompanyFinancialSnapshots.Add(snapshot);
        }
    }

    private CompanyFinancialMetric UpdateState(
        MutableFinancialState state,
        IndustryTrend industryTrend)
    {
        var changed = CompanyFinancialMetric.None;
        var bands = chanceRates.RandomMagnitudeBands;

        // Metric order is an observable reproducibility contract: trigger, direction, then magnitude are drawn
        // together before the next metric, and companies are processed by ascending id.
        state.Revenue = UpdatePercentage(
            state.Revenue,
            bands.FinancialOperatingUpdateMin,
            bands.FinancialOperatingUpdateMax,
            industryTrend,
            favorableIncreases: true,
            nonNegative: true,
            MoneyPrecision,
            CompanyFinancialMetric.Revenue,
            ref changed);
        state.NetProfit = UpdatePercentage(
            state.NetProfit,
            bands.FinancialOperatingUpdateMin,
            bands.FinancialOperatingUpdateMax,
            industryTrend,
            favorableIncreases: true,
            nonNegative: false,
            MoneyPrecision,
            CompanyFinancialMetric.NetProfit,
            ref changed);
        state.OperatingCashFlow = UpdatePercentage(
            state.OperatingCashFlow,
            bands.FinancialOperatingUpdateMin,
            bands.FinancialOperatingUpdateMax,
            industryTrend,
            favorableIncreases: true,
            nonNegative: false,
            MoneyPrecision,
            CompanyFinancialMetric.OperatingCashFlow,
            ref changed);
        state.TotalAssets = UpdatePercentage(
            state.TotalAssets,
            bands.FinancialBalanceSheetUpdateMin,
            bands.FinancialBalanceSheetUpdateMax,
            industryTrend,
            favorableIncreases: true,
            nonNegative: true,
            MoneyPrecision,
            CompanyFinancialMetric.TotalAssets,
            ref changed);
        state.TotalLiabilities = UpdatePercentage(
            state.TotalLiabilities,
            bands.FinancialBalanceSheetUpdateMin,
            bands.FinancialBalanceSheetUpdateMax,
            industryTrend,
            favorableIncreases: false,
            nonNegative: true,
            MoneyPrecision,
            CompanyFinancialMetric.TotalLiabilities,
            ref changed);
        state.TotalDebt = UpdatePercentage(
            state.TotalDebt,
            bands.FinancialBalanceSheetUpdateMin,
            bands.FinancialBalanceSheetUpdateMax,
            industryTrend,
            favorableIncreases: false,
            nonNegative: true,
            MoneyPrecision,
            CompanyFinancialMetric.TotalDebt,
            ref changed);
        state.ExpectedDividendPerShare = UpdatePercentage(
            state.ExpectedDividendPerShare,
            bands.FinancialDividendUpdateMin,
            bands.FinancialDividendUpdateMax,
            industryTrend,
            favorableIncreases: true,
            nonNegative: true,
            RatioPrecision,
            CompanyFinancialMetric.ExpectedDividendPerShare,
            ref changed,
            MinimumPerShareChangeBase);
        state.BusinessRiskScore = UpdateScore(
            state.BusinessRiskScore,
            bands.FinancialRiskScoreUpdateMin,
            bands.FinancialRiskScoreUpdateMax,
            industryTrend,
            favorableIncreases: false,
            CompanyFinancialMetric.BusinessRisk,
            ref changed);
        state.ManagementRevenueForecast = UpdatePercentage(
            state.ManagementRevenueForecast,
            bands.FinancialForecastUpdateMin,
            bands.FinancialForecastUpdateMax,
            industryTrend,
            favorableIncreases: true,
            nonNegative: true,
            MoneyPrecision,
            CompanyFinancialMetric.ManagementRevenueForecast,
            ref changed);
        state.ManagementProfitForecast = UpdatePercentage(
            state.ManagementProfitForecast,
            bands.FinancialForecastUpdateMin,
            bands.FinancialForecastUpdateMax,
            industryTrend,
            favorableIncreases: true,
            nonNegative: false,
            MoneyPrecision,
            CompanyFinancialMetric.ManagementProfitForecast,
            ref changed);
        state.ManagementOperatingCashFlowForecast = UpdatePercentage(
            state.ManagementOperatingCashFlowForecast,
            bands.FinancialForecastUpdateMin,
            bands.FinancialForecastUpdateMax,
            industryTrend,
            favorableIncreases: true,
            nonNegative: false,
            MoneyPrecision,
            CompanyFinancialMetric.ManagementOperatingCashFlowForecast,
            ref changed);
        state.ManagementConfidenceScore = UpdateScore(
            state.ManagementConfidenceScore,
            bands.FinancialRiskScoreUpdateMin,
            bands.FinancialRiskScoreUpdateMax,
            industryTrend,
            favorableIncreases: true,
            CompanyFinancialMetric.ManagementConfidence,
            ref changed);

        return changed;
    }

    private decimal UpdatePercentage(
        decimal current,
        decimal minimum,
        decimal maximum,
        IndustryTrend industryTrend,
        bool favorableIncreases,
        bool nonNegative,
        int precision,
        CompanyFinancialMetric metric,
        ref CompanyFinancialMetric changed,
        decimal minimumChangeBase = MinimumMoneyChangeBase)
    {
        if (random.NextDouble() >= chanceRates.EventTriggerChances.FinancialMetricChange)
        {
            return current;
        }

        var favorable = random.NextDouble() < FavorableDirectionChance(industryTrend);
        var increase = favorable == favorableIncreases;
        var magnitude = DrawRange(minimum, maximum);
        var changeBase = Math.Max(Math.Abs(current), minimumChangeBase);
        var next = current + (increase ? 1m : -1m) * changeBase * magnitude;
        if (nonNegative)
        {
            next = Math.Max(0m, next);
        }

        changed |= metric;
        return Math.Round(next, precision, MidpointRounding.AwayFromZero);
    }

    private decimal UpdateScore(
        decimal current,
        decimal minimum,
        decimal maximum,
        IndustryTrend industryTrend,
        bool favorableIncreases,
        CompanyFinancialMetric metric,
        ref CompanyFinancialMetric changed)
    {
        if (random.NextDouble() >= chanceRates.EventTriggerChances.FinancialMetricChange)
        {
            return current;
        }

        var favorable = random.NextDouble() < FavorableDirectionChance(industryTrend);
        var increase = favorable == favorableIncreases;
        var next = current + (increase ? 1m : -1m) * DrawRange(minimum, maximum);
        changed |= metric;
        return Ratio(Math.Clamp(next, 0m, 100m));
    }

    private void EnforceUpdateInvariants(
        MutableFinancialState state,
        int issuedSharesCount,
        ref CompanyFinancialMetric changed)
    {
        if (state.TotalDebt > state.TotalLiabilities)
        {
            state.TotalDebt = state.TotalLiabilities;
            changed |= CompanyFinancialMetric.TotalDebt;
        }

        state.ExpectedDividendPool = Money(
            state.ExpectedDividendPerShare * issuedSharesCount);
        state.DividendCoverageRatio = DividendCoverage(
            state.NetProfit,
            state.ExpectedDividendPool);

        ClampForecast(
            state.Revenue,
            ref state.ManagementRevenueForecast,
            CompanyFinancialMetric.ManagementRevenueForecast,
            ref changed,
            nonNegative: true);
        ClampForecast(
            state.NetProfit,
            ref state.ManagementProfitForecast,
            CompanyFinancialMetric.ManagementProfitForecast,
            ref changed,
            nonNegative: false);
        ClampForecast(
            state.OperatingCashFlow,
            ref state.ManagementOperatingCashFlowForecast,
            CompanyFinancialMetric.ManagementOperatingCashFlowForecast,
            ref changed,
            nonNegative: false);
    }

    private void ClampForecast(
        decimal actual,
        ref decimal forecast,
        CompanyFinancialMetric metric,
        ref CompanyFinancialMetric changed,
        bool nonNegative)
    {
        var deviation = Math.Abs(actual) * financialOptions.MaximumForecastDeviationRatio;
        var minimum = actual - deviation;
        var maximum = actual + deviation;
        var clamped = Money(Math.Clamp(forecast, minimum, maximum));
        if (nonNegative)
        {
            clamped = Math.Max(0m, clamped);
        }

        if (clamped != forecast)
        {
            forecast = clamped;
            changed |= metric;
        }
    }

    private CompanyFinancialSnapshotMoment? MomentFor(int tradingCycleNumber)
    {
        if (tradingCycleNumber == 1)
        {
            return CompanyFinancialSnapshotMoment.DayOpening;
        }

        return tradingCycleNumber == clockOptions.TradingCyclesPerDay / 2 + 1
            ? CompanyFinancialSnapshotMoment.Midday
            : null;
    }

    private double FavorableDirectionChance(IndustryTrend trend)
    {
        var impulse = (double)financialOptions.IndustryImpulseWeight;
        return trend switch
        {
            IndustryTrend.Rising => Math.Clamp(0.5d + impulse, 0d, 1d),
            IndustryTrend.Plateau => 0.5d,
            IndustryTrend.Falling => Math.Clamp(0.5d - impulse, 0d, 1d),
            _ => throw new ArgumentOutOfRangeException(nameof(trend), trend, "Unknown industry trend."),
        };
    }

    private async Task<IndustryTrend> IndustryTrendForAsync(int industryId)
    {
        var sentiment = await dbContext.Industries
            .AsNoTracking()
            .Where(industry => industry.Id == industryId)
            .Select(industry => (int?)industry.SentimentValue)
            .SingleOrDefaultAsync();
        return TrendFor(sentiment ?? 0);
    }

    private async Task<int?> LatestDividendEventIdAsync(int companyId) =>
        await dbContext.CompanyDividendEvents
            .AsNoTracking()
            .Where(dividendEvent => dividendEvent.CompanyId == companyId)
            .OrderByDescending(dividendEvent => dividendEvent.TradingDayNumber)
            .ThenByDescending(dividendEvent => dividendEvent.Id)
            .Select(dividendEvent => (int?)dividendEvent.Id)
            .FirstOrDefaultAsync();

    private async Task<Dictionary<int, int?>> LatestDividendEventIdsAsync(
        IReadOnlyCollection<int> companyIds)
    {
        var events = await dbContext.CompanyDividendEvents
            .AsNoTracking()
            .Where(dividendEvent => companyIds.Contains(dividendEvent.CompanyId))
            .OrderBy(dividendEvent => dividendEvent.CompanyId)
            .ThenBy(dividendEvent => dividendEvent.TradingDayNumber)
            .ThenBy(dividendEvent => dividendEvent.Id)
            .Select(dividendEvent => new
            {
                dividendEvent.CompanyId,
                dividendEvent.Id,
            })
            .ToListAsync();
        return events
            .GroupBy(dividendEvent => dividendEvent.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => (int?)group.Last().Id);
    }

    private CompanyFinancialSnapshot? LocalSnapshot(
        int companyId,
        int tradingDayNumber,
        CompanyFinancialSnapshotMoment moment) =>
        dbContext.ChangeTracker
            .Entries<CompanyFinancialSnapshot>()
            .Where(entry => entry.State != EntityState.Deleted)
            .Select(entry => entry.Entity)
            .SingleOrDefault(snapshot =>
                snapshot.CompanyId == companyId
                && snapshot.TradingDayNumber == tradingDayNumber
                && snapshot.Moment == moment);

    private decimal DrawSeedForecastDeviation(
        RandomMagnitudeBands magnitudes) =>
        Math.Clamp(
            DrawRange(
                magnitudes.FinancialSeedManagementForecastDeviationMin,
                magnitudes.FinancialSeedManagementForecastDeviationMax),
            -financialOptions.MaximumForecastDeviationRatio,
            financialOptions.MaximumForecastDeviationRatio);

    private decimal DrawRange(decimal minimum, decimal maximum) =>
        minimum + (decimal)random.NextDouble() * (maximum - minimum);

    private decimal Forecast(decimal actual, decimal deviation) =>
        Money(actual * (1m + deviation));

    private static decimal DividendCoverage(decimal netProfit, decimal dividendPool) =>
        dividendPool <= 0m
            ? 0m
            : Ratio(Math.Max(0m, netProfit) / dividendPool);

    private static decimal Money(decimal value) =>
        Math.Round(value, MoneyPrecision, MidpointRounding.AwayFromZero);

    private static decimal Ratio(decimal value) =>
        Math.Round(value, RatioPrecision, MidpointRounding.AwayFromZero);

    private static IndustryTrend TrendFor(int sentimentValue) =>
        sentimentValue switch
        {
            > 0 => IndustryTrend.Rising,
            < 0 => IndustryTrend.Falling,
            _ => IndustryTrend.Plateau,
        };

    private static CompanyFinancialHistoryPoint HistoryPoint(
        CompanyFinancialSnapshot snapshot) =>
        new(
            StateOf(snapshot),
            snapshot.TradingDayNumber,
            snapshot.Moment,
            snapshot.CreatedInCycleId,
            snapshot.CreatedAt,
            snapshot.Id);

    private static CompanyFinancialState StateOf(
        CompanyFinancialSnapshot snapshot) =>
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

    private static CompanyFinancialSnapshot CreateSnapshot(
        int companyId,
        int currentCycleId,
        int tradingDayNumber,
        CompanyFinancialSnapshotMoment moment,
        DateTime now,
        CompanyFinancialState state,
        CompanyFinancialScoringResult score,
        int? latestDividendEventId,
        CompanyFinancialMetric changedMetrics) =>
        new()
        {
            CompanyId = companyId,
            CreatedInCycleId = currentCycleId,
            TradingDayNumber = tradingDayNumber,
            Moment = moment,
            CreatedAt = now,
            Revenue = state.Revenue,
            NetProfit = state.NetProfit,
            OperatingCashFlow = state.OperatingCashFlow,
            TotalAssets = state.TotalAssets,
            TotalLiabilities = state.TotalLiabilities,
            TotalDebt = state.TotalDebt,
            ExpectedDividendPerShare = state.ExpectedDividendPerShare,
            ExpectedDividendPool = state.ExpectedDividendPool,
            DividendCoverageRatio = state.DividendCoverageRatio,
            LatestDividendEventId = latestDividendEventId,
            BusinessRiskScore = state.BusinessRiskScore,
            ManagementRevenueForecast = state.ManagementRevenueForecast,
            ManagementProfitForecast = state.ManagementProfitForecast,
            ManagementOperatingCashFlowForecast = state.ManagementOperatingCashFlowForecast,
            ManagementOutlook = score.ManagementOutlook,
            ManagementConfidenceScore = state.ManagementConfidenceScore,
            ProfitabilityScore = score.ProfitabilityScore,
            ProfitabilityLevel = score.ProfitabilityLevel,
            StabilityScore = score.StabilityScore,
            FinancialVolatilityLevel = score.FinancialVolatilityLevel,
            ClosureRiskScore = score.ClosureRiskScore,
            ClosureRiskLevel = score.ClosureRiskLevel,
            ChangedMetrics = changedMetrics,
        };

    private sealed record FinancialCompany(
        int Id,
        int IssuedSharesCount,
        int SentimentValue)
    {
        public IndustryTrend IndustryTrend => TrendFor(SentimentValue);
    }

    private sealed class MutableFinancialState
    {
        public decimal Revenue;
        public decimal NetProfit;
        public decimal OperatingCashFlow;
        public decimal TotalAssets;
        public decimal TotalLiabilities;
        public decimal TotalDebt;
        public decimal ExpectedDividendPerShare;
        public decimal ExpectedDividendPool;
        public decimal DividendCoverageRatio;
        public decimal BusinessRiskScore;
        public decimal ManagementRevenueForecast;
        public decimal ManagementProfitForecast;
        public decimal ManagementOperatingCashFlowForecast;
        public decimal ManagementConfidenceScore;

        public static MutableFinancialState From(
            CompanyFinancialSnapshot snapshot) =>
            new()
            {
                Revenue = snapshot.Revenue,
                NetProfit = snapshot.NetProfit,
                OperatingCashFlow = snapshot.OperatingCashFlow,
                TotalAssets = snapshot.TotalAssets,
                TotalLiabilities = snapshot.TotalLiabilities,
                TotalDebt = snapshot.TotalDebt,
                ExpectedDividendPerShare = snapshot.ExpectedDividendPerShare,
                ExpectedDividendPool = snapshot.ExpectedDividendPool,
                DividendCoverageRatio = snapshot.DividendCoverageRatio,
                BusinessRiskScore = snapshot.BusinessRiskScore,
                ManagementRevenueForecast = snapshot.ManagementRevenueForecast,
                ManagementProfitForecast = snapshot.ManagementProfitForecast,
                ManagementOperatingCashFlowForecast =
                    snapshot.ManagementOperatingCashFlowForecast,
                ManagementConfidenceScore = snapshot.ManagementConfidenceScore,
            };

        public CompanyFinancialState ToImmutable() =>
            new(
                Revenue,
                NetProfit,
                OperatingCashFlow,
                TotalAssets,
                TotalLiabilities,
                TotalDebt,
                ExpectedDividendPerShare,
                ExpectedDividendPool,
                DividendCoverageRatio,
                BusinessRiskScore,
                ManagementRevenueForecast,
                ManagementProfitForecast,
                ManagementOperatingCashFlowForecast,
                ManagementConfidenceScore);
    }
}
