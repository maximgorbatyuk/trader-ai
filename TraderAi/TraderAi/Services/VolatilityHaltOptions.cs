namespace TraderAi.Services;

public sealed class VolatilityHaltOptions
{
    public const string SectionName = "VolatilityHalt";

    // Opt-in: the volatility halt stays off unless this is set to true.
    public bool Enabled { get; set; }

    public int ReferenceWindowSeconds { get; set; } = 300;

    public int LimitStateDurationSeconds { get; set; } = 15;

    public int TradingPauseDurationSeconds { get; set; } = 300;

    public decimal UpperBandPercent { get; set; } = 5m;

    public decimal LowerBandPercent { get; set; } = 5m;
}
