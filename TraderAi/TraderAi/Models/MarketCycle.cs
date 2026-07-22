namespace TraderAi.Models;

public sealed class MarketCycle
{
    public int Id { get; set; }

    public int MarketRunId { get; set; }

    public int CycleNumber { get; set; }

    public int TradingDayId { get; set; }

    public int TradingCycleNumber { get; set; }

    public CycleStatus Status { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
