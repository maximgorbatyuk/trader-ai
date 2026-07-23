using Microsoft.Extensions.Options;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record CompanyAuditScoringInput(
    decimal AdjustedReturnPercent,
    decimal MaximumAdjustedCycleMovePercent,
    decimal FreeShareDilutionPercent,
    int StockSplitCount,
    int ReverseSplitCount,
    DividendFundingOutcome? LatestDividendOutcome,
    decimal ModeledMaximumDividend,
    decimal DividendCoverageRatio,
    IndustryTrend IndustryTrend,
    CompanyMetricLevel ProfitabilityLevel,
    CompanyMetricLevel FinancialVolatilityLevel,
    CompanyMetricLevel ClosureRiskLevel,
    ManagementOutlook ManagementOutlook,
    decimal ManagementConfidenceScore,
    decimal OperatingCashFlow);

public sealed record CompanyAuditScoringResult(
    int AdjustedReturnScore,
    int CycleJumpScore,
    int FreeShareEmissionScore,
    int DenominationScore,
    int DividendOutcomeScore,
    int DividendCoverageScore,
    int IndustryScore,
    int ProfitabilityFactorScore,
    int StabilityFactorScore,
    int ClosureRiskFactorScore,
    int ManagementOutlookFactorScore,
    int TotalScore,
    CompanyRiskRating Rating);

public sealed class CompanyAuditScorer
{
    private const decimal CoveredDividendRatio = 1m;
    private const decimal MaximumNormalizedScore = 100m;

    private readonly ScoringConfiguration configuration;

    public CompanyAuditScorer(IOptions<AuditorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Value.IsValid())
        {
            throw new ArgumentException("Auditor scoring options are invalid.", nameof(options));
        }

        configuration = ScoringConfiguration.From(options.Value);
    }

    public CompanyAuditScoringResult Score(CompanyAuditScoringInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        var adjustedReturnScore = AdjustedReturnScore(input.AdjustedReturnPercent);
        var cycleJumpScore = CycleJumpScore(input.MaximumAdjustedCycleMovePercent);
        var freeShareEmissionScore = FreeShareEmissionScore(input.FreeShareDilutionPercent);
        var denominationScore = DenominationScore(input.StockSplitCount, input.ReverseSplitCount);
        var dividendOutcomeScore = DividendOutcomeScore(input.LatestDividendOutcome);
        var dividendCovered = input.ModeledMaximumDividend == 0m
            || input.DividendCoverageRatio >= CoveredDividendRatio;
        var dividendCoverageScore = dividendCovered
            ? configuration.DividendCoveredScore
            : configuration.DividendUncoveredScore;
        var industryScore = IndustryScore(input.IndustryTrend);
        var profitabilityFactorScore = ProfitabilityScore(input.ProfitabilityLevel);
        var stabilityFactorScore = VolatilityScore(input.FinancialVolatilityLevel);
        var closureRiskFactorScore = ClosureRiskScore(input.ClosureRiskLevel);
        var managementOutlookFactorScore = ManagementOutlookScore(
            input.ManagementOutlook,
            input.ManagementConfidenceScore);

        var unclampedTotal = adjustedReturnScore
            + cycleJumpScore
            + freeShareEmissionScore
            + denominationScore
            + dividendOutcomeScore
            + dividendCoverageScore
            + industryScore
            + profitabilityFactorScore
            + stabilityFactorScore
            + closureRiskFactorScore
            + managementOutlookFactorScore;
        var totalScore = Math.Clamp(
            unclampedTotal,
            configuration.MinimumTotalScore,
            configuration.MaximumTotalScore);
        var rating = RatingFor(
            totalScore,
            dividendCovered,
            input.OperatingCashFlow,
            input.ClosureRiskLevel);

        return new CompanyAuditScoringResult(
            adjustedReturnScore,
            cycleJumpScore,
            freeShareEmissionScore,
            denominationScore,
            dividendOutcomeScore,
            dividendCoverageScore,
            industryScore,
            profitabilityFactorScore,
            stabilityFactorScore,
            closureRiskFactorScore,
            managementOutlookFactorScore,
            totalScore,
            rating);
    }

    private int AdjustedReturnScore(decimal adjustedReturnPercent)
    {
        if (adjustedReturnPercent >= configuration.StrongAdjustedReturnPercent)
        {
            return configuration.StrongPositiveReturnScore;
        }

        if (adjustedReturnPercent >= configuration.ModerateAdjustedReturnPercent)
        {
            return configuration.ModeratePositiveReturnScore;
        }

        if (adjustedReturnPercent <= -configuration.StrongAdjustedReturnPercent)
        {
            return configuration.StrongNegativeReturnScore;
        }

        return adjustedReturnPercent < -configuration.ModerateAdjustedReturnPercent
            ? configuration.ModerateNegativeReturnScore
            : 0;
    }

    private int CycleJumpScore(decimal maximumAdjustedCycleMovePercent)
    {
        if (maximumAdjustedCycleMovePercent >= configuration.StrongCycleJumpPercent)
        {
            return configuration.StrongCycleJumpScore;
        }

        return maximumAdjustedCycleMovePercent >= configuration.ModerateCycleJumpPercent
            ? configuration.ModerateCycleJumpScore
            : 0;
    }

    private int FreeShareEmissionScore(decimal freeShareDilutionPercent)
    {
        if (freeShareDilutionPercent <= 0m)
        {
            return 0;
        }

        return freeShareDilutionPercent <= configuration.ModerateFreeShareDilutionPercent
            ? configuration.ModerateFreeShareEmissionScore
            : configuration.StrongFreeShareEmissionScore;
    }

    private int DenominationScore(int stockSplitCount, int reverseSplitCount)
    {
        var score = (long)stockSplitCount * configuration.StockSplitScore
            + (long)reverseSplitCount * configuration.ReverseSplitScore;

        return (int)Math.Clamp(
            score,
            configuration.MinimumDenominationScore,
            configuration.MaximumDenominationScore);
    }

    private int DividendOutcomeScore(DividendFundingOutcome? outcome) =>
        outcome switch
        {
            null => 0,
            DividendFundingOutcome.Paid => configuration.DividendPaidScore,
            DividendFundingOutcome.Reduced => configuration.DividendReducedScore,
            DividendFundingOutcome.Skipped => configuration.DividendSkippedScore,
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unknown dividend funding outcome."),
        };

    private int IndustryScore(IndustryTrend trend) =>
        trend switch
        {
            IndustryTrend.Rising => configuration.IndustryRisingScore,
            IndustryTrend.Plateau => 0,
            IndustryTrend.Falling => configuration.IndustryFallingScore,
            _ => throw new ArgumentOutOfRangeException(
                nameof(trend),
                trend,
                "Unknown industry trend."),
        };

    private int ProfitabilityScore(CompanyMetricLevel level) =>
        level switch
        {
            CompanyMetricLevel.High => configuration.HighProfitabilityScore,
            CompanyMetricLevel.Medium => configuration.MediumProfitabilityScore,
            CompanyMetricLevel.Low => configuration.LowProfitabilityScore,
            _ => throw UnknownLevel(nameof(level), level),
        };

    private int VolatilityScore(CompanyMetricLevel level) =>
        level switch
        {
            CompanyMetricLevel.Low => configuration.LowVolatilityScore,
            CompanyMetricLevel.Medium => configuration.MediumVolatilityScore,
            CompanyMetricLevel.High => configuration.HighVolatilityScore,
            _ => throw UnknownLevel(nameof(level), level),
        };

    private int ClosureRiskScore(CompanyMetricLevel level) =>
        level switch
        {
            CompanyMetricLevel.Low => configuration.LowClosureRiskScore,
            CompanyMetricLevel.Medium => configuration.MediumClosureRiskScore,
            CompanyMetricLevel.High => configuration.HighClosureRiskScore,
            _ => throw UnknownLevel(nameof(level), level),
        };

    private int ManagementOutlookScore(ManagementOutlook outlook, decimal confidence)
    {
        var configuredScore = outlook switch
        {
            ManagementOutlook.Positive => configuration.PositiveManagementOutlookScore,
            ManagementOutlook.Neutral => configuration.NeutralManagementOutlookScore,
            ManagementOutlook.Negative => configuration.NegativeManagementOutlookScore,
            _ => throw new ArgumentOutOfRangeException(
                nameof(outlook),
                outlook,
                "Unknown management outlook."),
        };

        return decimal.ToInt32(decimal.Round(
            configuredScore * confidence / MaximumNormalizedScore,
            0,
            MidpointRounding.AwayFromZero));
    }

    private CompanyRiskRating RatingFor(
        int totalScore,
        bool dividendCovered,
        decimal operatingCashFlow,
        CompanyMetricLevel closureRiskLevel)
    {
        if (totalScore >= configuration.ExtraRaisedExpectationsThreshold)
        {
            return CompanyRiskRating.ExtraRaisedExpectations;
        }

        if (totalScore >= configuration.RaisedExpectationsThreshold)
        {
            return CompanyRiskRating.RaisedExpectations;
        }

        if (totalScore <= configuration.HighRiskThreshold)
        {
            return CompanyRiskRating.HighRisk;
        }

        if (totalScore <= configuration.LowRiskThreshold)
        {
            return CompanyRiskRating.LowRisk;
        }

        return dividendCovered
            && operatingCashFlow >= 0m
            && closureRiskLevel != CompanyMetricLevel.High
                ? CompanyRiskRating.Stable
                : CompanyRiskRating.LowRisk;
    }

    private static void Validate(CompanyAuditScoringInput input)
    {
        if (input.MaximumAdjustedCycleMovePercent < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "Maximum adjusted cycle movement cannot be negative.");
        }

        if (input.FreeShareDilutionPercent < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "Free-share dilution cannot be negative.");
        }

        if (input.StockSplitCount < 0 || input.ReverseSplitCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "Denomination event counts cannot be negative.");
        }

        if (input.ModeledMaximumDividend < 0m || input.DividendCoverageRatio < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "Modeled dividends and their coverage cannot be negative.");
        }

        if (input.ManagementConfidenceScore is < 0m or > MaximumNormalizedScore)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "Management confidence must be between 0 and 100.");
        }
    }

    private static ArgumentOutOfRangeException UnknownLevel(
        string parameterName,
        CompanyMetricLevel level) =>
        new(parameterName, level, "Unknown company metric level.");

    private sealed record ScoringConfiguration(
        decimal ModerateAdjustedReturnPercent,
        decimal StrongAdjustedReturnPercent,
        decimal ModerateCycleJumpPercent,
        decimal StrongCycleJumpPercent,
        decimal ModerateFreeShareDilutionPercent,
        int StrongPositiveReturnScore,
        int ModeratePositiveReturnScore,
        int ModerateNegativeReturnScore,
        int StrongNegativeReturnScore,
        int ModerateCycleJumpScore,
        int StrongCycleJumpScore,
        int ModerateFreeShareEmissionScore,
        int StrongFreeShareEmissionScore,
        int StockSplitScore,
        int ReverseSplitScore,
        int DividendPaidScore,
        int DividendReducedScore,
        int DividendSkippedScore,
        int DividendCoveredScore,
        int DividendUncoveredScore,
        int IndustryRisingScore,
        int IndustryFallingScore,
        int HighProfitabilityScore,
        int MediumProfitabilityScore,
        int LowProfitabilityScore,
        int LowVolatilityScore,
        int MediumVolatilityScore,
        int HighVolatilityScore,
        int LowClosureRiskScore,
        int MediumClosureRiskScore,
        int HighClosureRiskScore,
        int PositiveManagementOutlookScore,
        int NeutralManagementOutlookScore,
        int NegativeManagementOutlookScore,
        int MinimumDenominationScore,
        int MaximumDenominationScore,
        int MinimumTotalScore,
        int MaximumTotalScore,
        int ExtraRaisedExpectationsThreshold,
        int RaisedExpectationsThreshold,
        int LowRiskThreshold,
        int HighRiskThreshold)
    {
        public static ScoringConfiguration From(AuditorOptions options) =>
            new(
                options.ModerateAdjustedReturnPercent,
                options.StrongAdjustedReturnPercent,
                options.ModerateCycleJumpPercent,
                options.StrongCycleJumpPercent,
                options.ModerateFreeShareDilutionPercent,
                options.StrongPositiveReturnScore,
                options.ModeratePositiveReturnScore,
                options.ModerateNegativeReturnScore,
                options.StrongNegativeReturnScore,
                options.ModerateCycleJumpScore,
                options.StrongCycleJumpScore,
                options.ModerateFreeShareEmissionScore,
                options.StrongFreeShareEmissionScore,
                options.StockSplitScore,
                options.ReverseSplitScore,
                options.DividendPaidScore,
                options.DividendReducedScore,
                options.DividendSkippedScore,
                options.DividendCoveredScore,
                options.DividendUncoveredScore,
                options.IndustryRisingScore,
                options.IndustryFallingScore,
                options.HighProfitabilityScore,
                options.MediumProfitabilityScore,
                options.LowProfitabilityScore,
                options.LowVolatilityScore,
                options.MediumVolatilityScore,
                options.HighVolatilityScore,
                options.LowClosureRiskScore,
                options.MediumClosureRiskScore,
                options.HighClosureRiskScore,
                options.PositiveManagementOutlookScore,
                options.NeutralManagementOutlookScore,
                options.NegativeManagementOutlookScore,
                options.MinimumDenominationScore,
                options.MaximumDenominationScore,
                options.MinimumTotalScore,
                options.MaximumTotalScore,
                options.ExtraRaisedExpectationsThreshold,
                options.RaisedExpectationsThreshold,
                options.LowRiskThreshold,
                options.HighRiskThreshold);
    }
}
