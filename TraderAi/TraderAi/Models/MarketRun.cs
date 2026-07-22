namespace TraderAi.Models;

public sealed class MarketRun
{
    public int Id { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }
}
