namespace TraderAi.Services;

public sealed class CrisisOptions
{
    public const string SectionName = "Crisis";

    // Opt-in: crises stay off unless this is set to true.
    public bool Enabled { get; set; }
}
