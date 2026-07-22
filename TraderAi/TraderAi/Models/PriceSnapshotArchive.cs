namespace TraderAi.Models;

// Cold storage for price snapshots aged out of the live table. It is a plain column copy with no navigation
// so archiving is a bulk move; the running simulation never reads it, and it preserves the original Id.
public sealed class PriceSnapshotArchive
{
    public int Id { get; set; }

    public int? MarketRunId { get; set; }

    public int CompanyId { get; set; }

    public decimal Price { get; set; }

    public decimal? Capitalization { get; set; }

    public int? SourceShareTransactionId { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
