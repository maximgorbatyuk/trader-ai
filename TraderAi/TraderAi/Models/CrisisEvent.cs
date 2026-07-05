namespace TraderAi.Models;

// One recorded consequence of an active crisis: a sector shock at trigger time, an auditor's High/Extra
// verdict, or a trader bankruptcy that struck while the window was open. Rows accumulate on the crisis so
// its detail page can show a single timeline of everything that happened during it.
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
