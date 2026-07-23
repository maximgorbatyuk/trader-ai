using Microsoft.Extensions.Options;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class CompanyAuditScorerTests
{
    [Theory]
    [InlineData(-10, -3)]
    [InlineData(-9.999, -2)]
    [InlineData(-3, 0)]
    [InlineData(2.999, 0)]
    [InlineData(3, 2)]
    [InlineData(9.999, 2)]
    [InlineData(10, 3)]
    public void AdjustedReturnUsesApprovedBoundaries(double percent, int expected)
    {
        var result = CreateScorer().Score(NeutralInput() with
        {
            AdjustedReturnPercent = Convert.ToDecimal(percent),
        });

        Assert.Equal(expected, result.AdjustedReturnScore);
    }

    [Theory]
    [InlineData(4.999, 0)]
    [InlineData(5, -1)]
    [InlineData(9.999, -1)]
    [InlineData(10, -2)]
    public void MaximumAdjustedJumpUsesApprovedBoundaries(double percent, int expected)
    {
        var result = CreateScorer().Score(NeutralInput() with
        {
            MaximumAdjustedCycleMovePercent = Convert.ToDecimal(percent),
        });

        Assert.Equal(expected, result.CycleJumpScore);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.001, -1)]
    [InlineData(5, -1)]
    [InlineData(5.001, -2)]
    public void FreeShareDilutionUsesApprovedBoundary(double percent, int expected)
    {
        var result = CreateScorer().Score(NeutralInput() with
        {
            FreeShareDilutionPercent = Convert.ToDecimal(percent),
        });

        Assert.Equal(expected, result.FreeShareEmissionScore);
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(0, 1, -2)]
    [InlineData(1, 1, -1)]
    [InlineData(10, 0, 2)]
    [InlineData(0, 10, -4)]
    public void DenominationScoresEventsAndClampsTheirSum(
        int splitCount,
        int reverseSplitCount,
        int expected)
    {
        var result = CreateScorer().Score(NeutralInput() with
        {
            StockSplitCount = splitCount,
            ReverseSplitCount = reverseSplitCount,
        });

        Assert.Equal(expected, result.DenominationScore);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(DividendFundingOutcome.Paid, 1)]
    [InlineData(DividendFundingOutcome.Reduced, -2)]
    [InlineData(DividendFundingOutcome.Skipped, -3)]
    public void LatestActualDividendOutcomeUsesApprovedScore(
        DividendFundingOutcome? outcome,
        int expected)
    {
        var result = CreateScorer().Score(NeutralInput() with
        {
            LatestDividendOutcome = outcome,
        });

        Assert.Equal(expected, result.DividendOutcomeScore);
    }

    [Theory]
    [InlineData(0.999, -1)]
    [InlineData(1, 1)]
    public void ExpectedDividendCoverageUsesExactCashCoverageBoundary(
        double ratio,
        int expected)
    {
        var result = CreateScorer().Score(NeutralInput() with
        {
            DividendCoverageRatio = Convert.ToDecimal(ratio),
            ModeledMaximumDividend = 100m,
        });

        Assert.Equal(expected, result.DividendCoverageScore);
    }

    [Fact]
    public void ZeroModeledDividendIsCoveredWithoutAvailableCash()
    {
        var result = CreateScorer().Score(NeutralInput() with
        {
            ModeledMaximumDividend = 0m,
            DividendCoverageRatio = 0m,
        });

        Assert.Equal(1, result.DividendCoverageScore);
        Assert.Equal(CompanyRiskRating.Stable, result.Rating);
    }

    [Theory]
    [InlineData(IndustryTrend.Rising, 1)]
    [InlineData(IndustryTrend.Plateau, 0)]
    [InlineData(IndustryTrend.Falling, -1)]
    public void IndustryTrendUsesApprovedScore(IndustryTrend trend, int expected)
    {
        var result = CreateScorer().Score(NeutralInput() with
        {
            IndustryTrend = trend,
        });

        Assert.Equal(expected, result.IndustryScore);
    }

    [Theory]
    [InlineData(CompanyMetricLevel.High, 2)]
    [InlineData(CompanyMetricLevel.Medium, 0)]
    [InlineData(CompanyMetricLevel.Low, -2)]
    public void ProfitabilityLevelContributesConfiguredScore(
        CompanyMetricLevel level,
        int expected)
    {
        var result = CreateScorer().Score(NeutralInput() with
        {
            ProfitabilityLevel = level,
        });

        Assert.Equal(expected, result.ProfitabilityFactorScore);
    }

    [Theory]
    [InlineData(CompanyMetricLevel.Low, 1)]
    [InlineData(CompanyMetricLevel.Medium, 0)]
    [InlineData(CompanyMetricLevel.High, -2)]
    public void FinancialVolatilityLevelContributesConfiguredScore(
        CompanyMetricLevel level,
        int expected)
    {
        var result = CreateScorer().Score(NeutralInput() with
        {
            FinancialVolatilityLevel = level,
        });

        Assert.Equal(expected, result.StabilityFactorScore);
    }

    [Theory]
    [InlineData(CompanyMetricLevel.Low, 2)]
    [InlineData(CompanyMetricLevel.Medium, 0)]
    [InlineData(CompanyMetricLevel.High, -3)]
    public void ClosureRiskLevelContributesConfiguredScore(
        CompanyMetricLevel level,
        int expected)
    {
        var result = CreateScorer().Score(NeutralInput() with
        {
            ClosureRiskLevel = level,
        });

        Assert.Equal(expected, result.ClosureRiskFactorScore);
    }

    [Theory]
    [InlineData(ManagementOutlook.Positive, 100, 2)]
    [InlineData(ManagementOutlook.Positive, 50, 1)]
    [InlineData(ManagementOutlook.Positive, 0, 0)]
    [InlineData(ManagementOutlook.Neutral, 100, 0)]
    [InlineData(ManagementOutlook.Negative, 50, -1)]
    [InlineData(ManagementOutlook.Negative, 100, -2)]
    public void ManagementGuidanceIsScaledByConfidence(
        ManagementOutlook outlook,
        double confidence,
        int expected)
    {
        var result = CreateScorer().Score(NeutralInput() with
        {
            ManagementOutlook = outlook,
            ManagementConfidenceScore = Convert.ToDecimal(confidence),
        });

        Assert.Equal(expected, result.ManagementOutlookFactorScore);
    }

    [Theory]
    [InlineData(-5, CompanyRiskRating.HighRisk)]
    [InlineData(-2, CompanyRiskRating.LowRisk)]
    [InlineData(0, CompanyRiskRating.Stable)]
    [InlineData(2, CompanyRiskRating.RaisedExpectations)]
    [InlineData(5, CompanyRiskRating.ExtraRaisedExpectations)]
    public void TotalScoreUsesEveryExactStatusBoundary(
        int componentScore,
        CompanyRiskRating expected)
    {
        var options = ZeroScoringOptions();
        options.StrongPositiveReturnScore = componentScore;
        var result = CreateScorer(options).Score(NeutralInput() with
        {
            AdjustedReturnPercent = 10m,
        });

        Assert.Equal(componentScore, result.TotalScore);
        Assert.Equal(expected, result.Rating);
    }

    [Fact]
    public void TotalScoreIsClampedToConfiguredBounds()
    {
        var positiveOptions = ZeroScoringOptions();
        positiveOptions.StrongPositiveReturnScore = 20;
        positiveOptions.MinimumTotalScore = -6;
        positiveOptions.MaximumTotalScore = 6;
        var negativeOptions = ZeroScoringOptions();
        negativeOptions.StrongNegativeReturnScore = -20;
        negativeOptions.MinimumTotalScore = -6;
        negativeOptions.MaximumTotalScore = 6;

        var positive = CreateScorer(positiveOptions).Score(NeutralInput() with
        {
            AdjustedReturnPercent = 10m,
        });
        var negative = CreateScorer(negativeOptions).Score(NeutralInput() with
        {
            AdjustedReturnPercent = -10m,
        });

        Assert.Equal(6, positive.TotalScore);
        Assert.Equal(-6, negative.TotalScore);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void StableIsDowngradedWhenItsPaymentSafetyGateFails(
        bool expectedDividendCovered,
        bool negativeOperatingCashFlow,
        bool highClosureRisk)
    {
        var options = ZeroScoringOptions();
        var result = CreateScorer(options).Score(NeutralInput() with
        {
            DividendCoverageRatio = expectedDividendCovered ? 1m : 0.99m,
            OperatingCashFlow = negativeOperatingCashFlow ? -1m : 1m,
            ClosureRiskLevel = highClosureRisk
                ? CompanyMetricLevel.High
                : CompanyMetricLevel.Medium,
        });

        Assert.Equal(0, result.TotalScore);
        Assert.Equal(CompanyRiskRating.LowRisk, result.Rating);
    }

    [Fact]
    public void CoherentNeutralCompanyIsStable()
    {
        var result = CreateScorer(ZeroScoringOptions()).Score(NeutralInput());

        Assert.Equal(0, result.TotalScore);
        Assert.Equal(CompanyRiskRating.Stable, result.Rating);
    }

    private static CompanyAuditScorer CreateScorer(AuditorOptions? options = null) =>
        new(Options.Create(options ?? new AuditorOptions()));

    private static CompanyAuditScoringInput NeutralInput() =>
        new(
            AdjustedReturnPercent: 0m,
            MaximumAdjustedCycleMovePercent: 0m,
            FreeShareDilutionPercent: 0m,
            StockSplitCount: 0,
            ReverseSplitCount: 0,
            LatestDividendOutcome: null,
            ModeledMaximumDividend: 100m,
            DividendCoverageRatio: 1m,
            IndustryTrend: IndustryTrend.Plateau,
            ProfitabilityScore: 50m,
            ProfitabilityLevel: CompanyMetricLevel.Medium,
            FinancialStabilityScore: 50m,
            FinancialVolatilityLevel: CompanyMetricLevel.Medium,
            ClosureRiskScore: 50m,
            ClosureRiskLevel: CompanyMetricLevel.Medium,
            ManagementOutlook: ManagementOutlook.Neutral,
            ManagementConfidenceScore: 100m,
            OperatingCashFlow: 1m);

    private static AuditorOptions ZeroScoringOptions() =>
        new()
        {
            StrongPositiveReturnScore = 0,
            ModeratePositiveReturnScore = 0,
            ModerateNegativeReturnScore = 0,
            StrongNegativeReturnScore = 0,
            ModerateCycleJumpScore = 0,
            StrongCycleJumpScore = 0,
            ModerateFreeShareEmissionScore = 0,
            StrongFreeShareEmissionScore = 0,
            StockSplitScore = 0,
            ReverseSplitScore = 0,
            DividendPaidScore = 0,
            DividendReducedScore = 0,
            DividendSkippedScore = 0,
            DividendCoveredScore = 0,
            DividendUncoveredScore = 0,
            IndustryRisingScore = 0,
            IndustryFallingScore = 0,
            HighProfitabilityScore = 0,
            MediumProfitabilityScore = 0,
            LowProfitabilityScore = 0,
            LowVolatilityScore = 0,
            MediumVolatilityScore = 0,
            HighVolatilityScore = 0,
            LowClosureRiskScore = 0,
            MediumClosureRiskScore = 0,
            HighClosureRiskScore = 0,
            PositiveManagementOutlookScore = 0,
            NeutralManagementOutlookScore = 0,
            NegativeManagementOutlookScore = 0,
        };
}
