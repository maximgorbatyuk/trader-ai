namespace TraderAi.Models;

public sealed class Market
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public MarketStatus Status { get; set; }

    public int? CurrentCycleId { get; set; }

    // Cycle number at which the next dividend is paid; rescheduled to a fresh interval after each payout.
    public int NextDividendCycleNumber { get; set; }

    // Cycle number of the last crisis of each scope, the clock the next crisis chance ramps up from; zero
    // means none has happened yet.
    public int LastLocalCrisisCycleNumber { get; set; }

    public int LastGlobalCrisisCycleNumber { get; set; }

    // Cycle number of the last science investigation, the clock its next chance ramps up from; zero means
    // none has happened yet. Independent of the crisis clocks, so both may fire the same cycle.
    public int LastScienceInvestigationCycleNumber { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
