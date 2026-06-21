namespace TraderAi.Models;

// A sector grouping companies share, used as the unit a news event can move all at once.
public sealed class Industry
{
    public int Id { get; set; }

    public required string Name { get; set; }
}
