namespace TraderAi.Models;

// An append-only record of a member joining or leaving a collective fund, so a fund's or a member's history can
// be reconstructed for root-cause investigation. It captures the money that moved: the deposit handed over on a
// join, or the payout returned on a leave. The participant ids are plain scalars that tolerate a member whose
// participant row later left the market, mirroring the other history-tolerant tables.
public sealed class CollectiveFundMembershipEvent
{
    public int Id { get; set; }

    public int CollectiveFundId { get; set; }

    // The fund's own participant row; kept alongside CollectiveFundId so the fund's page can query and name events
    // by the same participant id the page is keyed on.
    public int FundParticipantId { get; set; }

    // The member who joined or left.
    public int ParticipantId { get; set; }

    public CollectiveFundMembershipEventType Type { get; set; }

    // Deposit contributed on a join, or payout returned on a leave (zero when a leaver's participant row was
    // already gone, so no cash could be returned).
    public decimal Amount { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
