namespace TraderAi.Services;

public sealed class NewsOptions
{
    public const string SectionName = "News";

    // Opt-in: automated news stays off unless this is set to true. Manual posts are unaffected.
    public bool Enabled { get; set; }

    // Automated news is published once every this many completed cycles.
    public int CyclesBetweenPosts { get; set; } = 25;

    // Chance an automated post carries market impact rather than being flavour only.
    public double ImpactProbability { get; set; } = 0.6;

    // When an automated post has impact, the chance it targets a single company rather than industries.
    public double CompanyScopeProbability { get; set; } = 0.5;

    public int MaxIndustriesPerPost { get; set; } = 3;
}
