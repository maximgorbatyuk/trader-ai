namespace TraderAi.Services;

public sealed class StockSplitOptions
{
    public const string SectionName = "StockSplit";

    // Opt-in: stock splits stay off unless this is set to true.
    public bool Enabled { get; set; }
}
