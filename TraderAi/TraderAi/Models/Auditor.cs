namespace TraderAi.Models;

// An independent rating agency that records evidence-backed company outlooks without trading or holding assets.
// It remains separate from Participant so scheduling cannot accidentally give it balances, positions, or debt.
public sealed class Auditor
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string Description { get; set; }

    public DateTime CreatedAt { get; set; }
}
