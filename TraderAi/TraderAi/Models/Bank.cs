namespace TraderAi.Models;

// The lender that issues loans. v1 keeps no balance of its own; it only defines the per-cycle interest rate a
// new loan snapshots at origination. One "National bank" is seeded on first usage.
public sealed class Bank
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public decimal InterestRatePerCycle { get; set; }
}
