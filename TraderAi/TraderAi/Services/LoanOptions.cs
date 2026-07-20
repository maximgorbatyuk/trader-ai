namespace TraderAi.Services;

// Structural loan settings (seed values and the amortization schedule). These are not chance/magnitude draws,
// so they live here rather than in RandomChanceRatesOptions.
public sealed class LoanOptions
{
    public const string SectionName = "Loan";

    // Opt-in: loan servicing and origination stay off unless this is set to true.
    public bool Enabled { get; set; }

    // The single bank seeded on first usage.
    public string BankName { get; set; } = "National bank";

    // Total interest collected over the whole loan, as a fraction of principal: the borrower repays principal + 10%.
    public decimal InterestRate { get; set; } = 0.10m;

    // A new loan's term in trading days is scaled by its size relative to the borrower's worth, clamped to this band.
    public int MinTermTradingDays { get; set; } = 5;
    public int MaxTermTradingDays { get; set; } = 20;

    // Loans created by the negative-balance migration use this fixed term.
    public int BackfillTermTradingDays { get; set; } = 10;

    // A missed or partial payment carries the unpaid amount to the next trading day inflated by this fine rate.
    public decimal MissedPaymentFineRate { get; set; } = 0.10m;

    // Inside this many trading days of the term, a borrower still in arrears is force-liquidated to raise cash.
    public int DistressWindowTradingDays { get; set; } = 2;

    // A collective fund short on cash to return a departing member's deposit borrows the shortfall inflated by
    // this fraction, so the payout clears with a small buffer instead of the fund force-selling shares up front.
    public decimal LeavePayoutLoanBufferRate { get; set; } = 0.10m;
}
