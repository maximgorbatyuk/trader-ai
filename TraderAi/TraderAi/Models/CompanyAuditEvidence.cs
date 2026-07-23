namespace TraderAi.Models;

public sealed class CompanyAuditEvidence
{
    public int CompanyRatingId { get; set; }

    public CompanyRating? CompanyRating { get; set; }

    public int CompanyId { get; set; }

    public int? CompanyFinancialSnapshotId { get; set; }

    public CompanyFinancialSnapshot? CompanyFinancialSnapshot { get; set; }

    public int EvaluationStartTradingDayNumber { get; set; }

    public int EvaluationEndTradingDayNumber { get; set; }

    public int EffectiveTradingDayNumber { get; set; }

    public int TotalScore { get; set; }

    public int AdjustedReturnScore { get; set; }

    public int CycleJumpScore { get; set; }

    public int FreeShareEmissionScore { get; set; }

    public int DenominationScore { get; set; }

    public int DividendOutcomeScore { get; set; }

    public int DividendCoverageScore { get; set; }

    public int IndustryScore { get; set; }

    public int ProfitabilityFactorScore { get; set; }

    public int StabilityFactorScore { get; set; }

    public int ClosureRiskFactorScore { get; set; }

    public int ManagementOutlookFactorScore { get; set; }

    public decimal StartPrice { get; set; }

    public decimal EndPrice { get; set; }

    public decimal AdjustedReturnPercent { get; set; }

    public decimal MaximumAdjustedCycleMovePercent { get; set; }

    public int OpeningIssuedShares { get; set; }

    public int EmittedShares { get; set; }

    public decimal FreeShareDilutionPercent { get; set; }

    public int StockSplitCount { get; set; }

    public int ReverseSplitCount { get; set; }

    public int? LatestDividendEventId { get; set; }

    public CompanyDividendEvent? LatestDividendEvent { get; set; }

    public decimal IssuerCash { get; set; }

    public decimal ModeledMaximumDividend { get; set; }

    public decimal DividendCoverageRatio { get; set; }

    public int? OpeningIndustrySentiment { get; set; }

    public int? ClosingIndustrySentiment { get; set; }

    public IndustryTrend IndustryTrend { get; set; }
}
