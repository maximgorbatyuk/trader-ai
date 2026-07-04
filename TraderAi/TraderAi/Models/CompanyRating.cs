namespace TraderAi.Models;

// One auditor's risk verdict on a company at a point in the simulation. The ordered set of these rows is a
// company's rating history: the newest row inside the safe period is the current rating, and comparing it to the
// row before gives the rating change shown on the company page.
public sealed class CompanyRating
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public int AuditorId { get; set; }

    public CompanyRiskRating Rating { get; set; }

    // The price drop applied when a hidden issue escalated the verdict to Extra; null otherwise.
    public decimal? ImpactPercent { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
