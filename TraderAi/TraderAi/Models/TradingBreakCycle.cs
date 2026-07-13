namespace TraderAi.Models;

public sealed class TradingBreakCycle
{
    public int Id { get; set; }

    public int TradingDayId { get; set; }

    public int StartedAfterCycleId { get; set; }

    public int ElapsedSeconds { get; set; }

    public int DurationSeconds { get; set; }

    public bool IsActive { get; set; }
}
