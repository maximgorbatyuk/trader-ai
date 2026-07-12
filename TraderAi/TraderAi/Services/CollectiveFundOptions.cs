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

    // Upper bound on members a single fund accepts; new joiners skip a fund once it reaches this. The default
    // matches the value this lived at as a service constant.
    public int MaxMembers { get; set; } = 20;

    // Operative member capacity: a fund closes to new joiners once it reaches this, and a fund found above it
    // returns its most recently joined member's deposit and drops them each cycle (by the standard leave rules)
    // until it is back within capacity. Clamped to MaxMembers and defaulting to it, so enforcement stays off
    // until this is set below MaxMembers.
    public int SoftCloseMembers { get; set; } = 20;
}
