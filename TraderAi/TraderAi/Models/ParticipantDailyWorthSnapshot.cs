namespace TraderAi.Models;

// One row per participant per closed trading day, capturing cash and holdings value at the day's close so the
// total-worth chart can show a long horizon of daily points instead of individual cycles. Net worth is derived
// on read as Balance plus HoldingsValue minus both loan and margin liability, matching the per-cycle snapshot.
public sealed class ParticipantDailyWorthSnapshot
{
    public int Id { get; set; }

    public int ParticipantId { get; set; }

    public int TradingDayId { get; set; }

    public decimal Balance { get; set; }

    public decimal HoldingsValue { get; set; }

    public decimal LoanLiability { get; set; }

    public decimal MarginLiability { get; set; }

    public DateTime CreatedAt { get; set; }
}
