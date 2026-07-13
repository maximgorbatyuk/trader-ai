namespace TraderAi.Services;

public sealed class VolatilityHaltOptions
{
    public const string SectionName = "VolatilityHalt";

    // Opt-in: the volatility halt stays off unless this is set to true.
    public bool Enabled { get; set; }

    public int ReferenceWindowSeconds { get; set; } = 300;

    public int LimitStateDurationSeconds { get; set; } = 15;

    public int TradingPauseDurationSeconds { get; set; } = 300;

    public decimal UpperBandPercent { get; set; } = 10m;

    public decimal LowerBandPercent { get; set; } = 15m;

    // The allowed participant order range is wider than the executable band: an order may rest anywhere between
    // reference -AllowedOrderLowerPercent and +AllowedOrderUpperPercent, waiting for the band to reach it.
    public decimal AllowedOrderLowerPercent { get; set; } = 25m;

    public decimal AllowedOrderUpperPercent { get; set; } = 15m;
}
