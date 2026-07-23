using Microsoft.Extensions.Options;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record CompanyFinancialState(
    decimal Revenue,
    decimal NetProfit,
    decimal OperatingCashFlow,
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal TotalDebt,
    decimal ExpectedDividendPerShare,
    decimal ExpectedDividendPool,
    decimal DividendCoverageRatio,
    decimal BusinessRiskScore,
    decimal ManagementRevenueForecast,
    decimal ManagementProfitForecast,
    decimal ManagementOperatingCashFlowForecast,
    decimal ManagementConfidenceScore);

public sealed record CompanyFinancialHistoryPoint(
    CompanyFinancialState State,
    int TradingDayNumber,
    CompanyFinancialSnapshotMoment Moment,
    int CreatedInCycleId,
    DateTime CreatedAt,
    int Id);

public sealed record CompanyFinancialScoringInput(
    CompanyFinancialState Current,
    IReadOnlyList<CompanyFinancialHistoryPoint> PriorSnapshots,
    IndustryTrend IndustryTrend);

public sealed record CompanyFinancialScoringResult(
    ManagementOutlook ManagementOutlook,
    decimal ProfitabilityScore,
    CompanyMetricLevel ProfitabilityLevel,
    decimal StabilityScore,
    CompanyMetricLevel FinancialVolatilityLevel,
    decimal ClosureRiskScore,
    CompanyMetricLevel ClosureRiskLevel);

public sealed class CompanyFinancialScorer
{
    private const double MinimumRatioDenominator = 0.000000000001d;
    private const double ManagementNeutralSignal = 0.02d;
    private const double NetMarginFullScale = 0.20d;
    private const double ReturnOnAssetsFullScale = 0.10d;
    private const double CashFlowRevenueFloorRatio = 0.05d;
    private const double RevenueTrendFullScale = 0.20d;
    private const double MaximumStabilityMetricChange = 0.30d;
    private const double EarningsHistoryShare = 0.80d;
    private const double NegativeGuidanceShare = 0.20d;
    private const double FullDebtLeverageRatio = 1d;
    private const decimal MinimumScore = 0m;
    private const decimal NeutralScore = 50m;
    private const decimal MaximumScore = 100m;
    private const int ScorePrecision = 6;

    private readonly ScoringConfiguration configuration;

    public CompanyFinancialScorer(IOptions<CompanyFinancialOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Value.IsValid())
        {
            throw new ArgumentException(
                "Company financial scoring options are invalid.",
                nameof(options));
        }

        configuration = ScoringConfiguration.From(options.Value);
    }

    public CompanyFinancialScoringResult Score(CompanyFinancialScoringInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Current);
        ArgumentNullException.ThrowIfNull(input.PriorSnapshots);

        var history = NormalizeHistory(input.PriorSnapshots);
        var outlook = CalculateManagementOutlook(input.Current);
        var profitability = CalculateProfitability(input.Current, history, outlook);
        var stability = CalculateStability(input.Current, history);
        var closureRisk = CalculateClosureRisk(
            input.Current,
            history,
            input.IndustryTrend,
            outlook);

        return new CompanyFinancialScoringResult(
            outlook,
            profitability,
            LevelFor(profitability),
            stability,
            Invert(LevelFor(stability)),
            closureRisk,
            LevelFor(closureRisk));
    }

    private CompanyFinancialHistoryPoint[] NormalizeHistory(
        IReadOnlyList<CompanyFinancialHistoryPoint> snapshots) =>
        snapshots
            .OrderBy(snapshot => snapshot.TradingDayNumber)
            .ThenBy(snapshot => snapshot.Moment)
            .ThenBy(snapshot => snapshot.CreatedInCycleId)
            .ThenBy(snapshot => snapshot.CreatedAt)
            .ThenBy(snapshot => snapshot.Id)
            .TakeLast(Math.Max(0, configuration.StabilityWindowSnapshots - 1))
            .ToArray();

    private static ManagementOutlook CalculateManagementOutlook(
        CompanyFinancialState current)
    {
        var confidence = UnitScore(current.ManagementConfidenceScore);
        var confidentDirections = new[]
        {
            SafeDirectionalChange(
                current.Revenue,
                current.ManagementRevenueForecast),
            SafeDirectionalChange(
                current.NetProfit,
                current.ManagementProfitForecast),
            SafeDirectionalChange(
                current.OperatingCashFlow,
                current.ManagementOperatingCashFlowForecast),
        }
            .Select(direction => direction * confidence)
            .ToArray();
        var hasPositiveDirection = confidentDirections
            .Any(direction => direction > ManagementNeutralSignal);
        var hasNegativeDirection = confidentDirections
            .Any(direction => direction < -ManagementNeutralSignal);
        if (hasPositiveDirection && hasNegativeDirection)
        {
            return ManagementOutlook.Neutral;
        }

        var confidentDirection = confidentDirections.Average();

        if (confidentDirection > ManagementNeutralSignal)
        {
            return ManagementOutlook.Positive;
        }

        if (confidentDirection < -ManagementNeutralSignal)
        {
            return ManagementOutlook.Negative;
        }

        return ManagementOutlook.Neutral;
    }

    private decimal CalculateProfitability(
        CompanyFinancialState current,
        IReadOnlyList<CompanyFinancialHistoryPoint> history,
        ManagementOutlook outlook)
    {
        var netMargin = CenteredRatioScore(
            SafeRatio(current.NetProfit, current.Revenue),
            NetMarginFullScale);
        var returnOnAssets = CenteredRatioScore(
            SafeRatio(current.NetProfit, current.TotalAssets),
            ReturnOnAssetsFullScale);
        var cashFlowDenominator = Math.Max(
            Math.Abs((double)current.NetProfit),
            Math.Abs((double)current.Revenue) * CashFlowRevenueFloorRatio);
        var cashFlowQuality = CenteredRatioScore(
            SafeRatio(current.OperatingCashFlow, cashFlowDenominator),
            1d);
        var revenueTrend = history.Count == 0
            ? NeutralScore
            : CenteredRatioScore(
                SafeDirectionalChange(history[^1].State.Revenue, current.Revenue),
                RevenueTrendFullScale);
        var management = ManagementScore(
            outlook,
            current.ManagementConfidenceScore);

        return ClampScore(
            netMargin * configuration.ProfitabilityNetMarginWeight
            + returnOnAssets * configuration.ProfitabilityReturnOnAssetsWeight
            + cashFlowQuality * configuration.ProfitabilityCashFlowWeight
            + revenueTrend * configuration.ProfitabilityRevenueTrendWeight
            + management * configuration.ProfitabilityManagementOutlookWeight);
    }

    private decimal CalculateStability(
        CompanyFinancialState current,
        IReadOnlyList<CompanyFinancialHistoryPoint> history)
    {
        if (history.Count == 0)
        {
            return NeutralScore;
        }

        var rows = history
            .Select(MetricValues)
            .Append(MetricValues(current))
            .ToArray();
        var changes = new List<double>((rows.Length - 1) * rows[0].Length);

        for (var rowIndex = 1; rowIndex < rows.Length; rowIndex++)
        {
            for (var metricIndex = 0; metricIndex < rows[rowIndex].Length; metricIndex++)
            {
                changes.Add(Math.Min(
                    MaximumStabilityMetricChange,
                    Math.Abs(SafeDirectionalChange(
                        rows[rowIndex - 1][metricIndex],
                        rows[rowIndex][metricIndex]))));
            }
        }

        var averageChange = changes.Average();
        return ClampScore(
            MaximumScore
            * (decimal)(1d - averageChange / MaximumStabilityMetricChange));
    }

    private decimal CalculateClosureRisk(
        CompanyFinancialState current,
        IReadOnlyList<CompanyFinancialHistoryPoint> history,
        IndustryTrend industryTrend,
        ManagementOutlook outlook)
    {
        var earningsRisk = EarningsAndCashFlowRisk(current, history, outlook);
        var leverageRisk = RatioRisk(
            SafeRatio(current.TotalDebt, current.TotalAssets),
            FullDebtLeverageRatio);
        var liabilitiesRisk = RatioRisk(
            SafeRatio(current.TotalLiabilities, current.TotalAssets),
            (double)configuration.MaximumLiabilitiesToAssetsRatio);
        var businessRisk = ClampScore(current.BusinessRiskScore);
        var industryRisk = industryTrend switch
        {
            IndustryTrend.Rising => MinimumScore,
            IndustryTrend.Falling => MaximumScore,
            _ => NeutralScore,
        };

        return ClampScore(
            earningsRisk * configuration.ClosureRiskEarningsAndCashFlowWeight
            + leverageRisk * configuration.ClosureRiskLeverageWeight
            + liabilitiesRisk * configuration.ClosureRiskLiabilitiesWeight
            + businessRisk * configuration.ClosureRiskBusinessWeight
            + industryRisk * configuration.ClosureRiskIndustryWeight);
    }

    private decimal EarningsAndCashFlowRisk(
        CompanyFinancialState current,
        IReadOnlyList<CompanyFinancialHistoryPoint> history,
        ManagementOutlook outlook)
    {
        var rows = history
            .Select(point => (
                point.State.NetProfit,
                point.State.OperatingCashFlow))
            .Append((current.NetProfit, current.OperatingCashFlow))
            .Reverse()
            .ToArray();
        var profitStreak = rows.TakeWhile(row => row.NetProfit < 0m).Count();
        var cashFlowStreak = rows.TakeWhile(row => row.OperatingCashFlow < 0m).Count();
        var negativeHistoryRatio =
            (profitStreak + cashFlowStreak) / (2d * rows.Length);
        var negativeGuidance = outlook == ManagementOutlook.Negative
            ? UnitScore(current.ManagementConfidenceScore)
            : 0d;

        return ClampScore(
            MaximumScore
            * (decimal)(
                negativeHistoryRatio * EarningsHistoryShare
                + negativeGuidance * NegativeGuidanceShare));
    }

    private static decimal ManagementScore(
        ManagementOutlook outlook,
        decimal confidenceScore)
    {
        var directionalConfidence = (decimal)UnitScore(confidenceScore) * NeutralScore;
        return outlook switch
        {
            ManagementOutlook.Positive => ClampScore(NeutralScore + directionalConfidence),
            ManagementOutlook.Negative => ClampScore(NeutralScore - directionalConfidence),
            _ => NeutralScore,
        };
    }

    private static decimal CenteredRatioScore(double ratio, double fullScale)
    {
        var normalized = Math.Clamp(ratio / fullScale, -1d, 1d);
        return ClampScore(NeutralScore + NeutralScore * (decimal)normalized);
    }

    private static decimal RatioRisk(double ratio, double fullRiskRatio) =>
        ClampScore(MaximumScore * (decimal)Math.Clamp(
            ratio / fullRiskRatio,
            0d,
            1d));

    private CompanyMetricLevel LevelFor(decimal score)
    {
        if (score <= configuration.LowLevelMaximumScore)
        {
            return CompanyMetricLevel.Low;
        }

        return score >= configuration.HighLevelMinimumScore
            ? CompanyMetricLevel.High
            : CompanyMetricLevel.Medium;
    }

    private static CompanyMetricLevel Invert(CompanyMetricLevel stabilityLevel) =>
        stabilityLevel switch
        {
            CompanyMetricLevel.Low => CompanyMetricLevel.High,
            CompanyMetricLevel.High => CompanyMetricLevel.Low,
            _ => CompanyMetricLevel.Medium,
        };

    private static decimal ClampScore(decimal score) =>
        Math.Round(
            Math.Clamp(score, MinimumScore, MaximumScore),
            ScorePrecision,
            MidpointRounding.AwayFromZero);

    private static double UnitScore(decimal score) =>
        Math.Clamp((double)score / (double)MaximumScore, 0d, 1d);

    private static double SafeRatio(decimal numerator, decimal denominator) =>
        SafeRatio(numerator, Math.Abs((double)denominator));

    private static double SafeRatio(decimal numerator, double denominator)
    {
        var convertedNumerator = (double)numerator;
        if (denominator <= MinimumRatioDenominator)
        {
            return convertedNumerator switch
            {
                > 0d => double.MaxValue,
                < 0d => -double.MaxValue,
                _ => 0d,
            };
        }

        return convertedNumerator / denominator;
    }

    private static double SafeDirectionalChange(decimal previous, decimal current)
    {
        var convertedPrevious = (double)previous;
        var convertedCurrent = (double)current;
        var denominator = Math.Max(
            Math.Abs(convertedPrevious),
            Math.Abs(convertedCurrent));
        if (denominator <= MinimumRatioDenominator)
        {
            return 0d;
        }

        return Math.Clamp(
            (convertedCurrent - convertedPrevious) / denominator,
            -1d,
            1d);
    }

    private static decimal[] MetricValues(CompanyFinancialState state) =>
    [
        state.Revenue,
        state.NetProfit,
        state.OperatingCashFlow,
        state.TotalAssets,
        state.TotalLiabilities,
        state.TotalDebt,
        state.ExpectedDividendPerShare,
        state.ExpectedDividendPool,
        state.DividendCoverageRatio,
        state.BusinessRiskScore,
        state.ManagementRevenueForecast,
        state.ManagementProfitForecast,
        state.ManagementOperatingCashFlowForecast,
        state.ManagementConfidenceScore,
    ];

    private static decimal[] MetricValues(CompanyFinancialHistoryPoint point) =>
        MetricValues(point.State);

    private sealed record ScoringConfiguration(
        int StabilityWindowSnapshots,
        decimal ProfitabilityNetMarginWeight,
        decimal ProfitabilityReturnOnAssetsWeight,
        decimal ProfitabilityCashFlowWeight,
        decimal ProfitabilityRevenueTrendWeight,
        decimal ProfitabilityManagementOutlookWeight,
        decimal ClosureRiskEarningsAndCashFlowWeight,
        decimal ClosureRiskLeverageWeight,
        decimal ClosureRiskLiabilitiesWeight,
        decimal ClosureRiskBusinessWeight,
        decimal ClosureRiskIndustryWeight,
        decimal LowLevelMaximumScore,
        decimal HighLevelMinimumScore,
        decimal MaximumLiabilitiesToAssetsRatio)
    {
        public static ScoringConfiguration From(CompanyFinancialOptions options) =>
            new(
                options.StabilityWindowSnapshots,
                options.ProfitabilityNetMarginWeight,
                options.ProfitabilityReturnOnAssetsWeight,
                options.ProfitabilityCashFlowWeight,
                options.ProfitabilityRevenueTrendWeight,
                options.ProfitabilityManagementOutlookWeight,
                options.ClosureRiskEarningsAndCashFlowWeight,
                options.ClosureRiskLeverageWeight,
                options.ClosureRiskLiabilitiesWeight,
                options.ClosureRiskBusinessWeight,
                options.ClosureRiskIndustryWeight,
                options.LowLevelMaximumScore,
                options.HighLevelMinimumScore,
                options.MaximumLiabilitiesToAssetsRatio);
    }
}
