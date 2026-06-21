namespace TraderAi.Models;

// A market shock that drives several industries down at once. A local crisis hits a few sectors; a global
// one hits a large share of them. Each affected industry is recorded with its own decrease in CrisisIndustry,
// and the title reads like a real headline so the event can be shown alongside the newswire.
public sealed class Crisis
{
    public int Id { get; set; }

    public required string Title { get; set; }

    public required string Content { get; set; }

    public CrisisScope Scope { get; set; }

    public int TriggeredInCycleId { get; set; }

    public DateTime TriggeredAt { get; set; }

    public ICollection<CrisisIndustry> Industries { get; set; } = new List<CrisisIndustry>();
}
