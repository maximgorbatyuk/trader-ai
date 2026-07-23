namespace TraderAi.Models;

public sealed class PortfolioAuditSummaryItem
{
    public int Id { get; set; }

    public int PortfolioAuditSummaryId { get; set; }

    public PortfolioAuditSummary? PortfolioAuditSummary { get; set; }

    public int CompanyId { get; set; }

    public int CompanyRatingId { get; set; }

    public CompanyRating? CompanyRating { get; set; }

    public int PlayerQuantity { get; set; }

    public int ManagedFundQuantity { get; set; }
}
