namespace TraderAi.Models;

// An upbeat market event — a research breakthrough or discovery — that lifts a few industries at once. It is
// the positive, always-local sibling of a crisis: it raises 1–5 sectors, each by its own increase recorded in
// ScienceInvestigationIndustry, and the title reads like a real headline so it can show alongside the newswire.
public sealed class ScienceInvestigation
{
    public int Id { get; set; }

    public required string Title { get; set; }

    public required string Content { get; set; }

    public int TriggeredInCycleId { get; set; }

    public DateTime TriggeredAt { get; set; }

    public ICollection<ScienceInvestigationIndustry> Industries { get; set; } = new List<ScienceInvestigationIndustry>();
}
