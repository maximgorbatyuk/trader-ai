using System.ComponentModel.DataAnnotations.Schema;

namespace TraderAi.Models;

// A borrower's explicit term debt, separate from revolving margin debit. It amortizes straight-line over a
// size-scaled term; missed payments classify overdue principal, interest, and fees without duplicating liability.
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

    // This classifies the overdue part of RemainingPrincipal and therefore must not be added to it as new debt.
    public decimal PastDuePrincipal { get; set; }

    public decimal PastDueInterest { get; set; }

    public decimal AccruedFees { get; set; }

    [NotMapped]
    public decimal TotalLiability => RemainingPrincipal + PastDueInterest + AccruedFees;

    // How many times a distress forced-sale has gone unsold and re-listed; each step deepens the discount.
    public int DistressDiscountStep { get; set; }

    public LoanStatus Status { get; set; }

    public int OpenedInCycleId { get; set; }

    public int? ClosedInCycleId { get; set; }

    public LoanCloseReason? CloseReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ClosedAt { get; set; }
}
