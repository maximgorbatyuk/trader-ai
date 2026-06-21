namespace TraderAi.Services;

public sealed class CollectiveFundOptions
{
    public const string SectionName = "CollectiveFund";

    // Opt-in: collective funds stay off unless this is set to true.
    public bool Enabled { get; set; }
}
