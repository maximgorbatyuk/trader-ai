namespace TraderAi.Services;

// A transaction fee skimmed from each participant-to-participant fill and paid to the National bank. It is a
// structural rate, not a chance/magnitude draw, so it lives here rather than in RandomChanceRatesOptions.
public sealed class TradeFeeOptions
{
    public const string SectionName = "TradeFee";

    // Opt-in: no fee is charged unless this is set to true.
    public bool Enabled { get; set; }

    // Fraction of the trade value (execution price × quantity) deducted from the seller's proceeds.
    public decimal FeeRate { get; set; } = 0.005m;

    // The bank the fee accrues to, resolved-or-created by the same first-by-id rule the loan service uses.
    public string BankName { get; set; } = "National bank";
}
