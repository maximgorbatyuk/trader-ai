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

    // Stores an absolute value because the rating determines direction and both positive and negative verdicts share this record.
    public decimal? ImpactPercent { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
