namespace TraderAi.Models;

// One recorded consequence of an active crisis: its trigger shock, or a bankruptcy or closure that happened
// while its window was open. Rows accumulate so the crisis detail can show a single consequence timeline.
public sealed class CrisisEvent
{
    public int Id { get; set; }

    public int CrisisId { get; set; }

    public CrisisEventType Type { get; set; }

    public required string Description { get; set; }

    // The company or industry the event concerns, when it concerns one; both null for a market-wide event.
    public int? CompanyId { get; set; }

    public int? IndustryId { get; set; }

    // The price move (percent) the event carried, when it carried one.
    public decimal? ImpactPercent { get; set; }

    public int CreatedInCycleId { get; set; }

    public int CreatedInCycleNumber { get; set; }

    public DateTime CreatedAt { get; set; }
}
