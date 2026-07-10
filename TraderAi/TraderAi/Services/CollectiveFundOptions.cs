namespace TraderAi.Services;

public sealed class CollectiveFundOptions
{
    public const string SectionName = "CollectiveFund";

    // Opt-in: collective funds stay off unless this is set to true.
    public bool Enabled { get; set; }

    // Only traders whose cash sits below this line are candidates to pool into a fund. Raising it widens the
    // pool of would-be joiners; the default matches the value this lived at as a service constant.
    public decimal JoinBalanceCeiling { get; set; } = 500_000m;

    // How many snapshots back the fund-growth trend (join boost and celebratory newswire) looks; the default
    // matches the value this lived at as a service constant.
    public int FundGrowthWindowCycles { get; set; } = 5;
}
