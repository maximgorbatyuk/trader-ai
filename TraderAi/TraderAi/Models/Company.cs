namespace TraderAi.Models;

public sealed class Company
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public int IndustryId { get; set; }

    public int IssuedSharesCount { get; set; }

    // Capitalisation recorded at this company's most recent dividend window, the baseline the next window's
    // stability test compares against. Refreshed every window; null until the first one.
    public decimal? LastDividendCapitalization { get; set; }

    // Cycle the company was listed. Seeded companies carry the first cycle; companies that appear later carry the
    // cycle they were minted in. Null on rows that predate the column.
    public int? CreatedInCycleId { get; set; }

    // A closed company is delisted: its orders are cancelled and holdings zeroed, and it is filtered out of the
    // live roster, the map, and every per-cycle service. The row is kept (not deleted) so history and deep-links
    // still resolve. Null while the company is live.
    public int? ClosedInCycleId { get; set; }

    public DateTime? ClosedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
