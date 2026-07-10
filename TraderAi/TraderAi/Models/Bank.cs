namespace TraderAi.Models;

// The lender that issues loans and the sink that collects trade transaction fees. It defines the per-cycle
// interest rate a new loan snapshots at origination and accrues fees into Balance. One "National bank" is
// seeded on first usage.
public sealed class Bank
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public decimal InterestRatePerCycle { get; set; }

    // Accumulated lender revenue — trade transaction fees plus loan interest; a passive sink that only grows.
    public decimal Balance { get; set; }
}
