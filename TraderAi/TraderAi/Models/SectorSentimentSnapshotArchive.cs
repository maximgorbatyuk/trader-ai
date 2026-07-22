namespace TraderAi.Models;

// Cold storage keeps old sentiment history out of the simulation's working set while preserving original ids.
public sealed class SectorSentimentSnapshotArchive
{
    public int Id { get; set; }

    public int? MarketRunId { get; set; }

    public int IndustryId { get; set; }

    public int SentimentValue { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
