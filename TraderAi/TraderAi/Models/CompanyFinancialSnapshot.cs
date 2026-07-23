namespace TraderAi.Models;

public sealed class CompanyFinancialSnapshot
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public int CreatedInCycleId { get; set; }

    public int TradingDayNumber { get; set; }

    public CompanyFinancialSnapshotMoment Moment { get; set; }

    public DateTime CreatedAt { get; set; }

    public decimal Revenue { get; set; }

    public decimal NetProfit { get; set; }

    public decimal OperatingCashFlow { get; set; }

    public decimal TotalAssets { get; set; }

    public decimal TotalLiabilities { get; set; }

    public decimal TotalDebt { get; set; }

    public decimal ExpectedDividendPerShare { get; set; }

    public decimal ExpectedDividendPool { get; set; }

    public decimal DividendCoverageRatio { get; set; }

    public int? LatestDividendEventId { get; set; }

    public CompanyDividendEvent? LatestDividendEvent { get; set; }

    public decimal BusinessRiskScore { get; set; }

    public decimal ManagementRevenueForecast { get; set; }

    public decimal ManagementProfitForecast { get; set; }

    public decimal ManagementOperatingCashFlowForecast { get; set; }

    public ManagementOutlook ManagementOutlook { get; set; }

    public decimal ManagementConfidenceScore { get; set; }

    public decimal ProfitabilityScore { get; set; }

    public CompanyMetricLevel ProfitabilityLevel { get; set; }

    public decimal StabilityScore { get; set; }

    public CompanyMetricLevel FinancialVolatilityLevel { get; set; }

    public decimal ClosureRiskScore { get; set; }

    public CompanyMetricLevel ClosureRiskLevel { get; set; }

    public CompanyFinancialMetric ChangedMetrics { get; set; }
}
