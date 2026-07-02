namespace TraderAi.Models;

// One row per completed cycle per human player, capturing cash and holdings value at the cycle's
// completion so the UI can show per-cycle money and worth changes.
public sealed class ParticipantWorthSnapshot
{
    public int Id { get; set; }

    public int ParticipantId { get; set; }

    public int CreatedInCycleId { get; set; }

    public decimal Balance { get; set; }

    public decimal HoldingsValue { get; set; }

    public DateTime CreatedAt { get; set; }
}
