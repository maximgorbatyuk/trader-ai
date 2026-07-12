namespace TraderAi.Models;

// A pooled investment vehicle that trades as its own participant. Members hand it 90% of their cash and stop
// buying for themselves, drawing pass-through dividends instead; the fund returns a member's deposit when they
// leave and splits its remaining cash among the survivors when it unwinds. This record holds the fund-side
// lifecycle state, while each membership lives in a CollectiveFundParticipant row.
public sealed class CollectiveFund
{
    public int Id { get; set; }

    // The participant row the fund trades through: its balance is the pooled capital and it owns the shares.
    public int ParticipantId { get; set; }

    // The member who opened the fund; informational only, since the founder can leave like anyone else.
    public int FoundedByParticipantId { get; set; }

    // A fund the human player opened and trades by hand. It is kept out of the AI decision pass and out of the
    // automatic idle/founder close, but stays a normal fund otherwise: others may still join it and be paid out.
    public bool IsPlayerManaged { get; set; }

    public CollectiveFundStatus Status { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    // Consecutive cycles the fund has owned no shares and cannot afford even the cheapest one; the fund unwinds
    // through the normal closing flow once it stays idle long enough, freeing its members to move on.
    public int IdleCycles { get; set; }

    // Highest net worth (cash plus holdings) the fund has ever reached, ratcheted up each cycle; the founder
    // closes the fund once its current worth collapses to a small fraction of this peak.
    public decimal PeakNetWorth { get; set; }

    // Cycle number of the last "fund is growing" newswire, so a fund on a long winning streak posts a fresh
    // headline only once per cooldown instead of every cycle. Null until the fund has ever posted one.
    public int? LastGrowthNewsInCycleNumber { get; set; }

    // How visible the fund is to would-be joiners: each paid advertisement lifts it by one, and it decays by one
    // each cycle the fund goes without advertising past the idle window, floored at zero. It biases fund-join
    // selection alongside size, worth, dividends, and growth.
    public int PopularityIndex { get; set; }

    // Cycle number of the fund's most recent paid advertisement; null until it has ever advertised. Popularity
    // only starts decaying once this is more than the idle window of cycles behind the current cycle.
    public int? LastAdvertisedInCycleNumber { get; set; }

    public ICollection<CollectiveFundParticipant> Members { get; set; } = new List<CollectiveFundParticipant>();
}
