namespace TraderAi.Models;

// A trader who left the market for good, archived after its Participant row is deleted so its history survives
// the removal. The name and figures are denormalised on purpose: the participant no longer exists to join to,
// so the departed-traders page and the bankruptcy-name fallback read everything they need straight from here.
public sealed class MarketExit
{
    public int Id { get; set; }

    // The id of the deleted participant row, kept so lingering references (e.g. bankruptcies) can resolve a name.
    public int ParticipantId { get; set; }

    public required string Name { get; set; }

    public MarketExitReason Reason { get; set; }

    // 0 when the trader was seeded before join-cycle tracking existed.
    public int JoinedInCycleId { get; set; }

    public int LeftInCycleId { get; set; }

    public int OrdersPlaced { get; set; }

    public decimal InitialBalance { get; set; }

    // High-water mark of cash plus holdings value reached while the trader was in the market.
    public decimal MaxTotalWorth { get; set; }

    // Cash balance at the moment of departure.
    public decimal QuitBalance { get; set; }

    public DateTime LeftAt { get; set; }
}
