using System.ComponentModel.DataAnnotations.Schema;

namespace TraderAi.Models;

// A borrower's explicit term debt, separate from revolving margin debit. It amortizes straight-line over a
// size-scaled term of trading days; missed payments classify overdue principal, interest, and fees without
// duplicating liability.
public sealed class Loan
{
    public int Id { get; set; }

    public int BankId { get; set; }

    public Bank? Bank { get; set; }

    // Scalar FK-by-id that tolerates an orphaned id after a participant hard-delete, like the history tables.
    public int ParticipantId { get; set; }

    // Taken money: the amount lent, including the cash buffer above the fill shortfall.
    public decimal Principal { get; set; }

    // Remain to pay: outstanding principal, reduced by each trading day's repayment.
    public decimal RemainingPrincipal { get; set; }

    // Snapshot of the bank's total-interest rate at origination, so a later bank-rate change never touches
    // existing loans. The whole term collects exactly Principal * InterestRate in interest.
    public decimal InterestRate { get; set; }

    // Scheduled interest not yet charged; drains by ScheduledInterestInstallment each serviced day so lifetime
    // interest is capped at Principal * InterestRate regardless of arrears.
    public decimal RemainingInterest { get; set; }

    public int TermTradingDays { get; set; }

    // Fixed straight-line principal charged per trading day: Principal / TermTradingDays.
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

    // The trading day the loan opened in; its term and due date are measured in trading days from here.
    public int OpenedInTradingDayId { get; set; }

    // The trading day this loan was last serviced, so end-of-day servicing charges each loan at most once per day
    // and skips the opening day (its first payment lands at the next day's close).
    public int? LastServicedTradingDayId { get; set; }

    public int? ClosedInCycleId { get; set; }

    public LoanCloseReason? CloseReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ClosedAt { get; set; }
}
