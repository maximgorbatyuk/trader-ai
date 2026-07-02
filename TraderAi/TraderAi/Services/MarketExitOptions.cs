namespace TraderAi.Services;

// Gates only the departure rolls and replacement spawning in MarketExitService. The fund idle-close and the
// devastating-loss flag live in CollectiveFundService under CollectiveFund.Enabled; while this is off those
// flags simply accumulate on participants and no one is ever removed.
public sealed class MarketExitOptions
{
    public const string SectionName = "MarketExit";

    // Opt-in: traders never leave the market unless this is set to true.
    public bool Enabled { get; set; }
}
