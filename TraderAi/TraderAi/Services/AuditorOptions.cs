namespace TraderAi.Services;

public sealed class AuditorOptions
{
    public const string SectionName = "Auditor";

    // Opt-in: auditing stays off unless this is set to true.
    public bool Enabled { get; set; }
}
