namespace TraderAi.Models;

// A randomly generated social-media style event. When it carries market impact it nudges the share price
// of either a single company or every company in one or more industries, recorded here so the move can be
// shown alongside its headline.
public sealed class NewsPost
{
    public int Id { get; set; }

    public required string Title { get; set; }

    public required string Content { get; set; }

    public int PublishedInCycleId { get; set; }

    public DateTime PublishedAt { get; set; }

    public NewsImpactScope Scope { get; set; }

    public NewsCategory Category { get; set; }

    // Direction and magnitude are set only when Scope is not None.
    public NewsImpactDirection? Direction { get; set; }

    // Percent of the share price moved: automated posts use up to 10, manual posts up to 95.
    public decimal? ImpactPercent { get; set; }

    // Set only for a single-company impact; industry impacts use the Industries join instead.
    public int? TargetCompanyId { get; set; }

    public ICollection<NewsPostIndustry> Industries { get; set; } = new List<NewsPostIndustry>();
}
