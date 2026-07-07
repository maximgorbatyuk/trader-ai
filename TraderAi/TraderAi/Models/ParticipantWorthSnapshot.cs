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

    // Open-loan liability (Σ RemainingPrincipal + Σ PastDueAmount) at snapshot time, so net worth can be
    // charted as Balance + HoldingsValue − LoanLiability.
    public decimal LoanLiability { get; set; }

    public DateTime CreatedAt { get; set; }
}
