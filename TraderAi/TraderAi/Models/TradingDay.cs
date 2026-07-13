namespace TraderAi.Models;

public sealed class TradingDay
{
    public int Id { get; set; }

    public int DayNumber { get; set; }

    public TradingSessionState State { get; set; }

    public int OpenedInCycleId { get; set; }

    public int? ClosedInCycleId { get; set; }
}
