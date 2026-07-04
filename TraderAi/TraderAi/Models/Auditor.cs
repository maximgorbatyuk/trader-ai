namespace TraderAi.Models;

// An independent rating agency that reviews one company per cycle and records a risk verdict. It never trades,
// holds shares, or carries a balance, so it is its own entity rather than a Participant. Its verdicts drive the
// company risk rating shown on the company page and can trigger a news-driven price correction.
public sealed class Auditor
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string Description { get; set; }

    public DateTime CreatedAt { get; set; }
}
