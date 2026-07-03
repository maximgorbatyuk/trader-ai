namespace TraderAi.Models;

public sealed class PriceSnapshot
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public decimal Price { get; set; }

    // Total capitalisation (price times issued shares) at the moment of the snapshot. Recorded going forward so
    // a capitalisation chart survives stock splits, where the price drops but the cap does not. Null on
    // snapshots taken before the field existed.
    public decimal? Capitalization { get; set; }

    public int? SourceShareTransactionId { get; set; }

    public ShareTransaction? SourceShareTransaction { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
