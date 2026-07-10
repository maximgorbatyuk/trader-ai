namespace TraderAi.Models;

public sealed class SectorSentimentSnapshot
{
    public int Id { get; set; }

    public int IndustryId { get; set; }

    public int SentimentValue { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
