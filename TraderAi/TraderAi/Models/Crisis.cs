namespace TraderAi.Models;

// A market shock that drives several industries down at once. A local crisis hits a few sectors; a global
// one hits a large share of them. Each affected industry is recorded with its own decrease in CrisisIndustry,
// and the title reads like a real headline so the event can be shown alongside the newswire. A crisis stays
// active for DurationCycles after the cycle it struck, and while active it makes auditors and bankruptcies
// bite harder and price-lifting events land less often; everything that happens in that window is recorded
// against it in Events for the crisis timeline.
public sealed class Crisis
{
    public int Id { get; set; }

    public required string Title { get; set; }

    public required string Content { get; set; }

    public CrisisScope Scope { get; set; }

    public int TriggeredInCycleId { get; set; }

    // Kept alongside the id so the active-window check needs no cycle join.
    public int TriggeredInCycleNumber { get; set; }

    // Cycles the crisis stays active after the one it struck; active while the current cycle is in
    // (TriggeredInCycleNumber, TriggeredInCycleNumber + DurationCycles].
    public int DurationCycles { get; set; }

    public DateTime TriggeredAt { get; set; }

    public ICollection<CrisisIndustry> Industries { get; set; } = new List<CrisisIndustry>();

    public ICollection<CrisisEvent> Events { get; set; } = new List<CrisisEvent>();
}
