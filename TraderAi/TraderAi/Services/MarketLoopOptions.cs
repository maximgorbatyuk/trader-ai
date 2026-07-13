namespace TraderAi.Services;

public sealed class MarketLoopOptions
{
    public const string SectionName = "MarketLoop";

    // Opt-in: the auto-advance loop stays dormant unless this is set to true.
    public bool Enabled { get; set; }

    public int IntervalSeconds { get; set; } = 2;
}
