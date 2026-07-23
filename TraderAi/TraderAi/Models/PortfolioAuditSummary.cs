namespace TraderAi.Models;

public sealed class PortfolioAuditSummary
{
    public int Id { get; set; }

    public int NewsPostId { get; set; }

    public NewsPost? NewsPost { get; set; }

    public int EvaluationStartTradingDayNumber { get; set; }

    public int EvaluationEndTradingDayNumber { get; set; }

    public int EffectiveTradingDayNumber { get; set; }

    public int ExtraRaisedExpectationsCount { get; set; }

    public int RaisedExpectationsCount { get; set; }

    public int StableCount { get; set; }

    public int LowRiskCount { get; set; }

    public int HighRiskCount { get; set; }

    public decimal AverageScore { get; set; }

    public PortfolioAuditDirection OverallDirection { get; set; }

    public DateTime CreatedAt { get; set; }

    public ICollection<PortfolioAuditSummaryItem> Items { get; set; } = new List<PortfolioAuditSummaryItem>();
}
