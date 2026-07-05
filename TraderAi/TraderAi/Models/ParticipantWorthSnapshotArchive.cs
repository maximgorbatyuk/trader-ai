namespace TraderAi.Models;

// Cold storage for worth snapshots aged out of the live table. It is a plain column copy so archiving is a
// bulk move; the running simulation never reads it, and it preserves the original Id.
public sealed class ParticipantWorthSnapshotArchive
{
    public int Id { get; set; }

    public int ParticipantId { get; set; }

    public int CreatedInCycleId { get; set; }

    public decimal Balance { get; set; }

    public decimal HoldingsValue { get; set; }

    public DateTime CreatedAt { get; set; }
}
