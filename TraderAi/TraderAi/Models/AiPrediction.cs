namespace TraderAi.Models;

public sealed class AiPrediction
{
    public long Id { get; set; }

    public long AiTraderCallId { get; set; }

    public int MarketRunId { get; set; }

    public int ParticipantId { get; set; }

    public int CompanyId { get; set; }

    public int SnapshotCycleNumber { get; set; }

    public int SnapshotTradingDayNumber { get; set; }

    public decimal BaselinePrice { get; set; }

    public AiPredictionDirection Direction { get; set; }

    public decimal Confidence { get; set; }

    public int HorizonCycles { get; set; }

    public decimal? TargetPrice { get; set; }

    public required string Reason { get; set; }

    public AiTraderCall? AiTraderCall { get; set; }
}
