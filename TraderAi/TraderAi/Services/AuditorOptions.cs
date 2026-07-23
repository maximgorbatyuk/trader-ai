namespace TraderAi.Services;

public sealed class AuditorOptions
{
    public const string SectionName = "Auditor";

    public bool Enabled { get; set; }

    public int AuditIntervalTradingDays { get; set; } = 2;

    public decimal ModerateAdjustedReturnPercent { get; set; } = 3m;

    public decimal StrongAdjustedReturnPercent { get; set; } = 10m;

    public decimal ModerateCycleJumpPercent { get; set; } = 5m;

    public decimal StrongCycleJumpPercent { get; set; } = 10m;

    public decimal ModerateFreeShareDilutionPercent { get; set; } = 5m;

    public decimal IndustryDirectionDeadband { get; set; } = 10m;

    public int StrongPositiveReturnScore { get; set; } = 3;

    public int ModeratePositiveReturnScore { get; set; } = 2;

    public int ModerateNegativeReturnScore { get; set; } = -2;

    public int StrongNegativeReturnScore { get; set; } = -3;

    public int ModerateCycleJumpScore { get; set; } = -1;

    public int StrongCycleJumpScore { get; set; } = -2;

    public int ModerateFreeShareEmissionScore { get; set; } = -1;

    public int StrongFreeShareEmissionScore { get; set; } = -2;

    public int StockSplitScore { get; set; } = 1;

    public int ReverseSplitScore { get; set; } = -2;

    public int DividendPaidScore { get; set; } = 1;

    public int DividendReducedScore { get; set; } = -2;

    public int DividendSkippedScore { get; set; } = -3;

    public int DividendCoveredScore { get; set; } = 1;

    public int DividendUncoveredScore { get; set; } = -1;

    public int IndustryRisingScore { get; set; } = 1;

    public int IndustryFallingScore { get; set; } = -1;

    public int ExtraRaisedExpectationsThreshold { get; set; } = 5;

    public int RaisedExpectationsThreshold { get; set; } = 2;

    public int LowRiskThreshold { get; set; } = -2;

    public int HighRiskThreshold { get; set; } = -5;

    public decimal ModerateDecisionPull { get; set; } = 0.05m;

    public decimal StrongDecisionPull { get; set; } = 0.10m;

    public bool IsValid()
        => AuditIntervalTradingDays >= 0
            && ModerateAdjustedReturnPercent > 0m
            && ModerateAdjustedReturnPercent < StrongAdjustedReturnPercent
            && StrongAdjustedReturnPercent <= 100m
            && ModerateCycleJumpPercent > 0m
            && ModerateCycleJumpPercent < StrongCycleJumpPercent
            && StrongCycleJumpPercent <= 100m
            && ModerateFreeShareDilutionPercent is >= 0m and <= 100m
            && IndustryDirectionDeadband >= 0m
            && HighRiskThreshold < LowRiskThreshold
            && LowRiskThreshold < RaisedExpectationsThreshold
            && RaisedExpectationsThreshold < ExtraRaisedExpectationsThreshold
            && ModerateDecisionPull is >= 0m and <= 1m
            && StrongDecisionPull is >= 0m and <= 1m
            && ModerateDecisionPull <= StrongDecisionPull;
}
