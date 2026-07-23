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
    public void OperatingTrendIncludesProfitDirectionWhenRevenueIsUnchanged()
    {
        var prior = BaseState();
        var improving = prior with { NetProfit = 125m };
        var worsening = prior with { NetProfit = 75m };
        var options = ProfitabilityOnly("RevenueTrend");

        var improvingResult = Score(improving, [Snapshot(prior, 1)], options);
        var worseningResult = Score(worsening, [Snapshot(prior, 1)], options);

        Assert.Equal(75m, improvingResult.ProfitabilityScore);
        Assert.Equal(25m, worseningResult.ProfitabilityScore);
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
    [InlineData(50, 37.5)]
    [InlineData(150, 87.5)]
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

    [Fact]
    public void LeverageRiskIncludesDebtToAssetsWhenDebtToLiabilitiesIsUnchanged()
    {
        var lowerAssets = BaseState() with
        {
            TotalAssets = 100m,
            TotalLiabilities = 100m,
            TotalDebt = 50m,
        };
        var higherAssets = lowerAssets with { TotalAssets = 200m };
        var options = ClosureRiskOnly("Leverage");

        Assert.Equal(50m, Score(lowerAssets, options: options).ClosureRiskScore);
        Assert.Equal(37.5m, Score(higherAssets, options: options).ClosureRiskScore);
    }

    [Fact]
    public void LeverageRiskIncludesDebtToLiabilitiesWhenDebtToAssetsIsUnchanged()
    {
        var lowerLiabilities = BaseState() with
        {
            TotalAssets = 100m,
            TotalLiabilities = 50m,
            TotalDebt = 50m,
        };
        var higherLiabilities = lowerLiabilities with { TotalLiabilities = 100m };
        var options = ClosureRiskOnly("Leverage");

        Assert.Equal(75m, Score(lowerLiabilities, options: options).ClosureRiskScore);
        Assert.Equal(50m, Score(higherLiabilities, options: options).ClosureRiskScore);
    }

    [Fact]
    public void LeverageRiskUsesConfiguredMaximumDebtToLiabilitiesRatio()
    {
        var state = BaseState() with
        {
            TotalAssets = 100m,
            TotalLiabilities = 100m,
            TotalDebt = 50m,
        };
        var options = ClosureRiskOnly("Leverage");
        options.MaximumDebtToLiabilitiesRatio = 0.50m;

        Assert.Equal(75m, Score(state, options: options).ClosureRiskScore);
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
        var profitHistory = Enumerable.Range(1, 5)
            .Select(sequence => Snapshot(profit, sequence))
            .ToArray();
        var lossHistory = Enumerable.Range(1, 5)
            .Select(sequence => Snapshot(loss, sequence))
            .ToArray();

        var oneLoss = Score(loss, profitHistory, options);
        var lossStreak = Score(loss, lossHistory, options);

        Assert.True(lossStreak.ClosureRiskScore > oneLoss.ClosureRiskScore);
        Assert.Equal(100m, lossStreak.ClosureRiskScore);
    }

    [Fact]
    public void NegativeStreakWarmsUpAgainstTheConfiguredWindow()
    {
        var healthy = BaseState() with
        {
            ManagementRevenueForecast = 1_000m,
            ManagementProfitForecast = 100m,
            ManagementOperatingCashFlowForecast = 110m,
            ManagementConfidenceScore = 100m,
        };
        var loss = healthy with
        {
            NetProfit = -100m,
            OperatingCashFlow = -100m,
            ManagementProfitForecast = -100m,
            ManagementOperatingCashFlowForecast = -100m,
        };
        var options = ClosureRiskOnly("EarningsAndCashFlow");
        var healthyHistory = Enumerable.Range(1, 5)
            .Select(sequence => Snapshot(healthy, sequence))
            .ToArray();
        var lossHistory = Enumerable.Range(1, 5)
            .Select(sequence => Snapshot(loss, sequence))
            .ToArray();

        var noLoss = Score(healthy, options: options);
        var firstLossWithoutHistory = Score(loss, options: options);
        var firstLossWithWarmHistory = Score(loss, healthyHistory, options);
        var fullLossWindow = Score(loss, lossHistory, options);

        Assert.Equal(0m, noLoss.ClosureRiskScore);
        Assert.Equal(13.333333m, firstLossWithoutHistory.ClosureRiskScore);
        Assert.Equal(firstLossWithoutHistory.ClosureRiskScore, firstLossWithWarmHistory.ClosureRiskScore);
        Assert.Equal(80m, fullLossWindow.ClosureRiskScore);
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

    [Fact]
    public void BusinessRiskBlendsCurrentLevelWithLatestDirection()
    {
        var current = BaseState() with { BusinessRiskScore = 50m };
        var worseningPrior = current with { BusinessRiskScore = 25m };
        var improvingPrior = current with { BusinessRiskScore = 100m };
        var options = ClosureRiskOnly("Business");

        var unchanged = Score(current, options: options);
        var worsening = Score(current, [Snapshot(worseningPrior, 1)], options);
        var improving = Score(current, [Snapshot(improvingPrior, 1)], options);

        Assert.Equal(50m, unchanged.ClosureRiskScore);
        Assert.Equal(62.5m, worsening.ClosureRiskScore);
        Assert.Equal(37.5m, improving.ClosureRiskScore);
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
    public void UnknownIndustryTrendIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Score(
                BaseState(),
                industryTrend: (IndustryTrend)999,
                options: ClosureRiskOnly("Industry")));
    }

    [Fact]
    public void HistoryOrderingUsesMomentThenCycleTimestampAndIdWithinADay()
    {
        var current = BaseState() with
        {
            Revenue = 1_100m,
            NetProfit = 110m,
        };
        var options = ProfitabilityOnly("RevenueTrend");
        options.StabilityWindowSnapshots = 7;
        var history = new[]
        {
            HistoryPoint(TrendState(1_000m), 4, CompanyFinancialSnapshotMoment.Midday, 2, 13, 2),
            HistoryPoint(TrendState(500m), 4, CompanyFinancialSnapshotMoment.Seed, 99, 15, 99),
            HistoryPoint(TrendState(900m), 4, CompanyFinancialSnapshotMoment.Midday, 2, 13, 1),
            HistoryPoint(TrendState(700m), 4, CompanyFinancialSnapshotMoment.DayOpening, 50, 14, 50),
            HistoryPoint(TrendState(850m), 4, CompanyFinancialSnapshotMoment.Midday, 2, 11, 2),
            HistoryPoint(TrendState(800m), 4, CompanyFinancialSnapshotMoment.Midday, 1, 12, 1),
        };

        var result = Score(current, history, options);

        Assert.Equal(72.727273m, result.ProfitabilityScore);
    }

    [Fact]
    public void HistoryIsTruncatedToTheConfiguredWindow()
    {
        var current = UniformState(100m);
        var options = new CompanyFinancialOptions
        {
            StabilityWindowSnapshots = 3,
        };
        var recentHistory = new[]
        {
            Snapshot(current, 3),
            Snapshot(current, 4),
        };
        var historyWithOldOutliers = new[]
        {
            Snapshot(UniformState(1_000m), 1),
            Snapshot(UniformState(10m), 2),
            recentHistory[0],
            recentHistory[1],
        };

        var recentResult = Score(current, recentHistory, options);
        var outlierResult = Score(current, historyWithOldOutliers, options);

        Assert.Equal(100m, recentResult.StabilityScore);
        Assert.Equal(recentResult.StabilityScore, outlierResult.StabilityScore);
        Assert.Equal(recentResult.ClosureRiskScore, outlierResult.ClosureRiskScore);
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

    private static CompanyFinancialState TrendState(decimal revenue) =>
        BaseState() with
        {
            Revenue = revenue,
            NetProfit = revenue / 10m,
        };

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
        HistoryPoint(
            state,
            sequence,
            CompanyFinancialSnapshotMoment.DayOpening,
            sequence,
            9,
            sequence);

    private static CompanyFinancialHistoryPoint HistoryPoint(
        CompanyFinancialState state,
        int tradingDay,
        CompanyFinancialSnapshotMoment moment,
        int createdInCycleId,
        int hour,
        int id) =>
        new(
            state,
            tradingDay,
            moment,
            createdInCycleId,
            new DateTime(2026, 7, tradingDay, hour, 0, 0, DateTimeKind.Utc),
            id);

    private static void AssertScoresAreBounded(CompanyFinancialScoringResult result)
    {
        Assert.InRange(result.ProfitabilityScore, 0m, 100m);
        Assert.InRange(result.StabilityScore, 0m, 100m);
        Assert.InRange(result.ClosureRiskScore, 0m, 100m);
    }
}
