namespace TraderAi.Models;

// A borrower's debt as an explicit liability rather than a negative balance. Opened when a margin buy fills
// for more cash than the buyer holds, it amortizes straight-line over a size-scaled term with per-cycle
// interest; a missed or partial payment accrues a fine into PastDueAmount. The debt is the set of open loans,
// so CurrentBalance is never left negative.
public sealed class Loan
{
    public int Id { get; set; }

    public int BankId { get; set; }

    public Bank? Bank { get; set; }

    // Scalar FK-by-id that tolerates an orphaned id after a participant hard-delete, like the history tables.
    public int ParticipantId { get; set; }

    // Taken money: the amount lent, including the cash buffer above the fill shortfall.
    public decimal Principal { get; set; }

    // Remain to pay: outstanding principal, reduced by each cycle's repayment.
    public decimal RemainingPrincipal { get; set; }

    // Snapshot of the bank's rate at origination, so a later bank-rate change never touches existing loans.
    public decimal InterestRatePerCycle { get; set; }

    public int TermCycles { get; set; }

    // Fixed straight-line principal charged per cycle: Principal / TermCycles.
    public decimal ScheduledInstallment { get; set; }

    // Arrears plus accrued fines carried from missed or partial payments.
    public decimal PastDueAmount { get; set; }

    // How many times a distress forced-sale has gone unsold and re-listed; each step deepens the discount.
    public int DistressDiscountStep { get; set; }

    public LoanStatus Status { get; set; }

    public int OpenedInCycleId { get; set; }

    public int? ClosedInCycleId { get; set; }

    public LoanCloseReason? CloseReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ClosedAt { get; set; }
}
