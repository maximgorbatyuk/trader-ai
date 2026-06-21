namespace TraderAi.Services;

public sealed class ScienceInvestigationOptions
{
    public const string SectionName = "ScienceInvestigation";

    // Opt-in: science investigations stay off unless this is set to true.
    public bool Enabled { get; set; }
}
