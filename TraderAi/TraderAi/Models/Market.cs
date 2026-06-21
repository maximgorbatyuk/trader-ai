namespace TraderAi.Models;

public sealed class Market
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public MarketStatus Status { get; set; }

    public int? CurrentCycleId { get; set; }

    // Cycle number at which the next dividend is paid; rescheduled to a fresh interval after each payout.
    public int NextDividendCycleNumber { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
