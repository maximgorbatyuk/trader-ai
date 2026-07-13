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

    // "5% per 50 cycles" → 0.05 / 50 = 0.001 per cycle.
    public decimal InterestRatePerCycle { get; set; } = 0.001m;

    // A new loan's term is scaled by its size relative to the borrower's worth, clamped to this band.
    public int MinTermCycles { get; set; } = 25;
    public int MaxTermCycles { get; set; } = 200;

    // Loans created by the negative-balance migration use this fixed term.
    public int BackfillTermCycles { get; set; } = 100;

    // A missed or partial payment carries the unpaid amount to next cycle inflated by this fine rate.
    public decimal MissedPaymentFineRate { get; set; } = 0.10m;

    // Inside this many cycles of the term, a borrower still in arrears is force-liquidated to raise cash.
    public int DistressWindowCycles { get; set; } = 15;

    // A collective fund short on cash to return a departing member's deposit borrows the shortfall inflated by
    // this fraction, so the payout clears with a small buffer instead of the fund force-selling shares up front.
    public decimal LeavePayoutLoanBufferRate { get; set; } = 0.10m;
}
