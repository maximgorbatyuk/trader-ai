namespace TraderAi.Models;

// One participant's membership in a collective fund. A participant may belong to at most one fund at a time:
// the schema permits more, but the service enforces zero-or-one. The row exists only while the member is in
// the fund — leaving or closing deletes it — and its presence is what stops the member buying for itself.
public sealed class CollectiveFundParticipant
{
    public int Id { get; set; }

    public int CollectiveFundId { get; set; }

    public int ParticipantId { get; set; }

    // When the member joined; also the moment self-buying was switched off for them.
    public DateTime JoinedAt { get; set; }

    public int JoinedInCycleId { get; set; }

    // Cash the member handed over on joining (90% of its balance then), returned in full when it leaves.
    public decimal DepositAmount { get; set; }

    // Consecutive cycles the member has sat at or above the leave line; the leave chance ramps with it.
    public int LeaveRampCycles { get; set; }

    // Set once the member has decided to leave but the fund has not yet freed enough cash to return the deposit.
    public bool IsLeaving { get; set; }
}
