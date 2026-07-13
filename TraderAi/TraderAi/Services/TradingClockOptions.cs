namespace TraderAi.Services;

public sealed class TradingClockOptions
{
    public const string SectionName = "TradingClock";

    public int TradingCyclesPerDay { get; set; } = 210;

    public int TradingCycleSeconds { get; set; } = 2;

    public int BreakDurationSeconds { get; set; } = 60;
}
