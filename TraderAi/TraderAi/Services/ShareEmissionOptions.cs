namespace TraderAi.Services;

public sealed class ShareEmissionOptions
{
    public const string SectionName = "ShareEmission";

    // Opt-in: free-share emission stays off unless this is set to true.
    public bool Enabled { get; set; }
}
