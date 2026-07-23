using Microsoft.Extensions.Options;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class CompanyFinancialScorerTests
{
    [Theory]
    [MemberData(nameof(ManagementOutlookCases))]
    public void ManagementOutlookUsesForecastDirectionAndConfidence(
        CompanyFinancialState state,
        ManagementOutlook expected)
    {
        var result = Score(state);

        Assert.Equal(expected, result.ManagementOutlook);
    }

    public static TheoryData<CompanyFinancialState, ManagementOutlook> ManagementOutlookCases =>
        new()
        {
            {
                BaseState() with
                {
                    ManagementRevenueForecast = 1_100m,
                    ManagementProfitForecast = 110m,
                    ManagementOperatingCashFlowForecast = 121m,
                    ManagementConfidenceScore = 90m,
                },
                ManagementOutlook.Positive
            },
            {
                BaseState() with
                {
                    ManagementRevenueForecast = 900m,
                    ManagementProfitForecast = 90m,
                    ManagementOperatingCashFlowForecast = 99m,
                    ManagementConfidenceScore = 90m,
                },
                ManagementOutlook.Negative
            },
            {
                BaseState() with
                {
                    ManagementRevenueForecast = 1_001m,
                    ManagementProfitForecast = 99.9m,
                    ManagementOperatingCashFlowForecast = 110m,
                    ManagementConfidenceScore = 100m,
                },
                ManagementOutlook.Neutral
            },
            {
                BaseState() with
                {
                    ManagementRevenueForecast = 1_100m,
                    ManagementProfitForecast = 110m,
                    ManagementOperatingCashFlowForecast = 121m,
                    ManagementConfidenceScore = 1m,
                },
                ManagementOutlook.Neutral
            },
            {
                BaseState() with
                {
                    ManagementRevenueForecast = 1_500m,
                    ManagementProfitForecast = 50m,
                    ManagementOperatingCashFlowForecast = 220m,
                    ManagementConfidenceScore = 100m,
                },
                ManagementOutlook.Neutral
            },
            {
                BaseState() with
                {
                    ManagementRevenueForecast = 500m,
                    ManagementProfitForecast = 200m,
                    ManagementOperatingCashFlowForecast = 55m,
                    ManagementConfidenceScore = 100m,
                },
                ManagementOutlook.Neutral
            },
            {
                BaseState() with
                {
                    ManagementRevenueForecast = 1_100m,
                    ManagementProfitForecast = 110m,
                    ManagementOperatingCashFlowForecast = 109.9m,
                    ManagementConfidenceScore = 100m,
                },
                ManagementOutlook.Positive
            },
        };

    [Theory]
    [InlineData("NetMargin")]
    [InlineData("ReturnOnAssets")]
    [InlineData("CashFlow")]
    [InlineData("RevenueTrend")]
    [InlineData("ManagementOutlook")]
    public void ProfitabilityComponentsRewardFinancialHealth(string component)
    {
        var healthy = BaseState();
        var unhealthy = BaseState();
        IReadOnlyList<CompanyFinancialHistoryPoint> healthyHistory = [];
        IReadOnlyList<CompanyFinancialHistoryPoint> unhealthyHistory = [];

        switch (component)
        {
            case "NetMargin":
                healthy = healthy with { NetProfit = 200m };
                unhealthy = unhealthy with { NetProfit = -200m };
                break;
            case "ReturnOnAssets":
                healthy = healthy with { NetProfit = 200m, TotalAssets = 500m };
                unhealthy = unhealthy with { NetProfit = -100m, TotalAssets = 500m };
                break;
            case "CashFlow":
                healthy = healthy with { OperatingCashFlow = 200m };
                unhealthy = unhealthy with { OperatingCashFlow = -200m };
                break;
            case "RevenueTrend":
                healthy = healthy with { Revenue = 1_250m };
                unhealthy = unhealthy with { Revenue = 750m };
                healthyHistory = [Snapshot(BaseState(), 1)];
                unhealthyHistory = [Snapshot(BaseState(), 1)];
                break;
            case "ManagementOutlook":
                healthy = healthy with
                {
                    ManagementRevenueForecast = 1_100m,
                    ManagementProfitForecast = 110m,
                    ManagementOperatingCashFlowForecast = 121m,
                    ManagementConfidenceScore = 80m,
                };
                unhealthy = unhealthy with
                {
                    ManagementRevenueForecast = 900m,
                    ManagementProfitForecast = 90m,
                    ManagementOperatingCashFlowForecast = 99m,
                    ManagementConfidenceScore = 80m,
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(component));
        }

        var options = ProfitabilityOnly(component);
        var healthyResult = Score(healthy, healthyHistory, options);
        var unhealthyResult = Score(unhealthy, unhealthyHistory, options);

        Assert.True(
            healthyResult.ProfitabilityScore > unhealthyResult.ProfitabilityScore,
            $"{component} should reward the healthier company.");
        Assert.InRange(healthyResult.ProfitabilityScore, options.HighLevelMinimumScore, 100m);
        Assert.InRange(unhealthyResult.ProfitabilityScore, 0m, options.LowLevelMaximumScore);
    }

    [Fact]
    public void StabilityIsNeutralWhenHistoryIsInsufficient()
    {
        var result = Score(BaseState());

        Assert.Equal(50m, result.StabilityScore);
        Assert.Equal(CompanyMetricLevel.Medium, result.FinancialVolatilityLevel);
    }

    [Theory]
    [InlineData(1.00, 100, CompanyMetricLevel.Low)]
    [InlineData(0.85, 50, CompanyMetricLevel.Medium)]
    [InlineData(0.50, 0, CompanyMetricLevel.High)]
    public void StabilityReflectsScheduledMetricMovement(
        double previousScale,
        double expectedStability,
        CompanyMetricLevel expectedVolatility)
    {
        var current = UniformState(100m);
        var prior = Scale(current, (decimal)previousScale);

        var result = Score(current, [Snapshot(prior, 1)]);

        Assert.Equal((decimal)expectedStability, result.StabilityScore);
        Assert.Equal(expectedVolatility, result.FinancialVolatilityLevel);
    }

    [Fact]
    public void StabilityIsIndependentOfHistoryInputOrdering()
    {
        var current = UniformState(100m);
        var oldest = Snapshot(UniformState(70m), 1);
        var newest = Snapshot(UniformState(85m), 2);

        var ascending = Score(current, [oldest, newest]);
        var descending = Score(current, [newest, oldest]);

        Assert.Equal(ascending.StabilityScore, descending.StabilityScore);
        Assert.Equal(ascending.FinancialVolatilityLevel, descending.FinancialVolatilityLevel);
    }

    [Fact]
    public void ZeroAndSignCrossingValuesRemainBounded()
    {
        var previous = UniformState(0m) with
        {
            NetProfit = -100m,
            OperatingCashFlow = 100m,
            ManagementProfitForecast = -100m,
        };
        var current = UniformState(0m) with
        {
            NetProfit = 100m,
            OperatingCashFlow = -100m,
            ManagementProfitForecast = 100m,
        };

        var result = Score(current, [Snapshot(previous, 1)]);

        AssertScoresAreBounded(result);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(150, 100)]
    public void ClosureRiskNormalizesDebtLeverage(
        double debtToAssetsPercent,
        double expectedScore)
    {
        var state = BaseState() with
        {
            TotalAssets = 100m,
            TotalLiabilities = 200m,
            TotalDebt = (decimal)debtToAssetsPercent,
        };

        var result = Score(
            state,
            options: ClosureRiskOnly("Leverage"));

        Assert.Equal((decimal)expectedScore, result.ClosureRiskScore);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(62.5, 50)]
    [InlineData(125, 100)]
    public void ClosureRiskNormalizesLiabilitiesAgainstConfiguredMaximum(
        double liabilities,
        double expectedScore)
    {
        var state = BaseState() with
        {
            TotalAssets = 100m,
            TotalLiabilities = (decimal)liabilities,
            TotalDebt = 0m,
        };

        var result = Score(
            state,
            options: ClosureRiskOnly("Liabilities"));

        Assert.Equal((decimal)expectedScore, result.ClosureRiskScore);
    }

    [Fact]
    public void ConsecutiveNegativeEarningsAndCashFlowIncreaseClosureRisk()
    {
        var loss = BaseState() with
        {
            NetProfit = -100m,
            OperatingCashFlow = -100m,
            ManagementRevenueForecast = 900m,
            ManagementProfitForecast = -110m,
            ManagementOperatingCashFlowForecast = -110m,
            ManagementConfidenceScore = 100m,
        };
        var profit = BaseState() with
        {
            NetProfit = 100m,
            OperatingCashFlow = 100m,
        };
        var options = ClosureRiskOnly("EarningsAndCashFlow");

        var oneLoss = Score(loss, [Snapshot(profit, 1), Snapshot(profit, 2)], options);
        var lossStreak = Score(loss, [Snapshot(loss, 1), Snapshot(loss, 2)], options);

        Assert.True(lossStreak.ClosureRiskScore > oneLoss.ClosureRiskScore);
        Assert.Equal(100m, lossStreak.ClosureRiskScore);
    }

    [Fact]
    public void NegativeGuidanceIncreasesEarningsClosureRisk()
    {
        var neutral = BaseState() with
        {
            ManagementRevenueForecast = 1_000m,
            ManagementProfitForecast = 100m,
            ManagementOperatingCashFlowForecast = 110m,
            ManagementConfidenceScore = 100m,
        };
        var negative = neutral with
        {
            ManagementRevenueForecast = 800m,
            ManagementProfitForecast = 80m,
            ManagementOperatingCashFlowForecast = 88m,
        };
        var options = ClosureRiskOnly("EarningsAndCashFlow");

        Assert.True(
            Score(negative, options: options).ClosureRiskScore
            > Score(neutral, options: options).ClosureRiskScore);
    }

    [Theory]
    [InlineData(IndustryTrend.Rising, 0)]
    [InlineData(IndustryTrend.Plateau, 50)]
    [InlineData(IndustryTrend.Falling, 100)]
    public void ClosureRiskReflectsIndustryDirection(
        IndustryTrend industryTrend,
        double expectedScore)
    {
        var result = Score(
            BaseState(),
            industryTrend: industryTrend,
            options: ClosureRiskOnly("Industry"));

        Assert.Equal((decimal)expectedScore, result.ClosureRiskScore);
    }

    [Fact]
    public void StrongBusinessHasLowerClosureRiskThanDistressedBusiness()
    {
        var strong = BaseState() with
        {
            NetProfit = 250m,
            OperatingCashFlow = 275m,
            TotalLiabilities = 150m,
            TotalDebt = 50m,
            BusinessRiskScore = 5m,
            ManagementRevenueForecast = 1_200m,
            ManagementProfitForecast = 300m,
            ManagementOperatingCashFlowForecast = 330m,
            ManagementConfidenceScore = 90m,
        };
        var distressed = BaseState() with
        {
            NetProfit = -250m,
            OperatingCashFlow = -300m,
            TotalLiabilities = 1_250m,
            TotalDebt = 1_100m,
            BusinessRiskScore = 100m,
            ManagementRevenueForecast = 700m,
            ManagementProfitForecast = -350m,
            ManagementOperatingCashFlowForecast = -400m,
            ManagementConfidenceScore = 100m,
        };

        var strongResult = Score(strong, industryTrend: IndustryTrend.Rising);
        var distressedResult = Score(distressed, industryTrend: IndustryTrend.Falling);

        Assert.Equal(CompanyMetricLevel.Low, strongResult.ClosureRiskLevel);
        Assert.Equal(CompanyMetricLevel.High, distressedResult.ClosureRiskLevel);
        Assert.True(strongResult.ClosureRiskScore < distressedResult.ClosureRiskScore);
    }

    [Theory]
    [InlineData(50, 75, CompanyMetricLevel.Low, CompanyMetricLevel.High)]
    [InlineData(49, 51, CompanyMetricLevel.Medium, CompanyMetricLevel.Medium)]
    [InlineData(25, 50, CompanyMetricLevel.High, CompanyMetricLevel.Low)]
    public void ExactConfiguredThresholdsMapEveryAggregateLevel(
        double lowMaximum,
        double highMinimum,
        CompanyMetricLevel expectedAggregateLevel,
        CompanyMetricLevel expectedVolatilityLevel)
    {
        var options = NeutralAggregateOptions(
            (decimal)lowMaximum,
            (decimal)highMinimum);
        var state = BaseState() with
        {
            ManagementRevenueForecast = 1_000m,
            ManagementProfitForecast = 100m,
            ManagementOperatingCashFlowForecast = 110m,
        };

        var result = Score(
            state,
            industryTrend: IndustryTrend.Plateau,
            options: options);

        Assert.Equal(50m, result.ProfitabilityScore);
        Assert.Equal(expectedAggregateLevel, result.ProfitabilityLevel);
        Assert.Equal(50m, result.StabilityScore);
        Assert.Equal(expectedVolatilityLevel, result.FinancialVolatilityLevel);
        Assert.Equal(50m, result.ClosureRiskScore);
        Assert.Equal(expectedAggregateLevel, result.ClosureRiskLevel);
    }

    [Fact]
    public void ExtremeInputsClampEveryScore()
    {
        const decimal extreme = 1_000_000_000_000_000_000m;
        var current = BaseState() with
        {
            Revenue = -extreme,
            NetProfit = extreme,
            OperatingCashFlow = -extreme,
            TotalAssets = 0m,
            TotalLiabilities = extreme,
            TotalDebt = extreme,
            ExpectedDividendPerShare = extreme,
            ExpectedDividendPool = extreme,
            DividendCoverageRatio = -extreme,
            BusinessRiskScore = extreme,
            ManagementRevenueForecast = extreme,
            ManagementProfitForecast = -extreme,
            ManagementOperatingCashFlowForecast = extreme,
            ManagementConfidenceScore = extreme,
        };
        var previous = current with
        {
            Revenue = extreme,
            NetProfit = -extreme,
            OperatingCashFlow = extreme,
            TotalAssets = extreme,
            TotalLiabilities = 0m,
            TotalDebt = 0m,
            BusinessRiskScore = -extreme,
            ManagementConfidenceScore = -extreme,
        };

        var result = Score(
            current,
            [Snapshot(previous, 1)],
            industryTrend: IndustryTrend.Falling);

        AssertScoresAreBounded(result);
    }

    private static CompanyFinancialScoringResult Score(
        CompanyFinancialState state,
        IReadOnlyList<CompanyFinancialHistoryPoint>? history = null,
        CompanyFinancialOptions? options = null,
        IndustryTrend industryTrend = IndustryTrend.Plateau)
    {
        var scorer = new CompanyFinancialScorer(
            Options.Create(options ?? new CompanyFinancialOptions()));
        return scorer.Score(new CompanyFinancialScoringInput(
            state,
            history ?? [],
            industryTrend));
    }

    private static CompanyFinancialOptions ProfitabilityOnly(string component)
    {
        var options = new CompanyFinancialOptions
        {
            ProfitabilityNetMarginWeight = 0m,
            ProfitabilityReturnOnAssetsWeight = 0m,
            ProfitabilityCashFlowWeight = 0m,
            ProfitabilityRevenueTrendWeight = 0m,
            ProfitabilityManagementOutlookWeight = 0m,
        };

        switch (component)
        {
            case "NetMargin":
                options.ProfitabilityNetMarginWeight = 1m;
                break;
            case "ReturnOnAssets":
                options.ProfitabilityReturnOnAssetsWeight = 1m;
                break;
            case "CashFlow":
                options.ProfitabilityCashFlowWeight = 1m;
                break;
            case "RevenueTrend":
                options.ProfitabilityRevenueTrendWeight = 1m;
                break;
            case "ManagementOutlook":
                options.ProfitabilityManagementOutlookWeight = 1m;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(component));
        }

        return options;
    }

    private static CompanyFinancialOptions ClosureRiskOnly(string component)
    {
        var options = new CompanyFinancialOptions
        {
            ClosureRiskEarningsAndCashFlowWeight = 0m,
            ClosureRiskLeverageWeight = 0m,
            ClosureRiskLiabilitiesWeight = 0m,
            ClosureRiskBusinessWeight = 0m,
            ClosureRiskIndustryWeight = 0m,
        };

        switch (component)
        {
            case "EarningsAndCashFlow":
                options.ClosureRiskEarningsAndCashFlowWeight = 1m;
                break;
            case "Leverage":
                options.ClosureRiskLeverageWeight = 1m;
                break;
            case "Liabilities":
                options.ClosureRiskLiabilitiesWeight = 1m;
                break;
            case "Business":
                options.ClosureRiskBusinessWeight = 1m;
                break;
            case "Industry":
                options.ClosureRiskIndustryWeight = 1m;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(component));
        }

        return options;
    }

    private static CompanyFinancialOptions NeutralAggregateOptions(
        decimal lowMaximum,
        decimal highMinimum) =>
        new()
        {
            ProfitabilityNetMarginWeight = 0m,
            ProfitabilityReturnOnAssetsWeight = 0m,
            ProfitabilityCashFlowWeight = 0m,
            ProfitabilityRevenueTrendWeight = 0m,
            ProfitabilityManagementOutlookWeight = 1m,
            ClosureRiskEarningsAndCashFlowWeight = 0m,
            ClosureRiskLeverageWeight = 0m,
            ClosureRiskLiabilitiesWeight = 0m,
            ClosureRiskBusinessWeight = 0m,
            ClosureRiskIndustryWeight = 1m,
            LowLevelMaximumScore = lowMaximum,
            HighLevelMinimumScore = highMinimum,
        };

    private static CompanyFinancialState BaseState() =>
        new(
            Revenue: 1_000m,
            NetProfit: 100m,
            OperatingCashFlow: 110m,
            TotalAssets: 1_000m,
            TotalLiabilities: 400m,
            TotalDebt: 200m,
            ExpectedDividendPerShare: 2m,
            ExpectedDividendPool: 40m,
            DividendCoverageRatio: 2.75m,
            BusinessRiskScore: 20m,
            ManagementRevenueForecast: 1_050m,
            ManagementProfitForecast: 105m,
            ManagementOperatingCashFlowForecast: 115m,
            ManagementConfidenceScore: 80m);

    private static CompanyFinancialState UniformState(decimal value) =>
        new(
            Revenue: value,
            NetProfit: value,
            OperatingCashFlow: value,
            TotalAssets: value,
            TotalLiabilities: value,
            TotalDebt: value,
            ExpectedDividendPerShare: value,
            ExpectedDividendPool: value,
            DividendCoverageRatio: value,
            BusinessRiskScore: value,
            ManagementRevenueForecast: value,
            ManagementProfitForecast: value,
            ManagementOperatingCashFlowForecast: value,
            ManagementConfidenceScore: value);

    private static CompanyFinancialState Scale(
        CompanyFinancialState state,
        decimal scale) =>
        new(
            state.Revenue * scale,
            state.NetProfit * scale,
            state.OperatingCashFlow * scale,
            state.TotalAssets * scale,
            state.TotalLiabilities * scale,
            state.TotalDebt * scale,
            state.ExpectedDividendPerShare * scale,
            state.ExpectedDividendPool * scale,
            state.DividendCoverageRatio * scale,
            state.BusinessRiskScore * scale,
            state.ManagementRevenueForecast * scale,
            state.ManagementProfitForecast * scale,
            state.ManagementOperatingCashFlowForecast * scale,
            state.ManagementConfidenceScore * scale);

    private static CompanyFinancialHistoryPoint Snapshot(
        CompanyFinancialState state,
        int sequence) =>
        new(
            state,
            sequence,
            CompanyFinancialSnapshotMoment.DayOpening,
            sequence,
            new DateTime(2026, 7, sequence, 9, 0, 0, DateTimeKind.Utc),
            sequence);

    private static void AssertScoresAreBounded(CompanyFinancialScoringResult result)
    {
        Assert.InRange(result.ProfitabilityScore, 0m, 100m);
        Assert.InRange(result.StabilityScore, 0m, 100m);
        Assert.InRange(result.ClosureRiskScore, 0m, 100m);
    }
}
