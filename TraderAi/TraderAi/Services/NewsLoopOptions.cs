namespace TraderAi.Services;

public sealed class NewsLoopOptions
{
    public const string SectionName = "NewsLoop";

    // Opt-in: the news loop stays dormant unless this is set to true.
    public bool Enabled { get; set; }

    public int IntervalSeconds { get; set; } = 7;

    // Chance a given tick publishes a post at all, then the chance a published post carries market impact.
    public double PublishProbability { get; set; } = 0.5;

    public double ImpactProbability { get; set; } = 0.6;

    // When a post has impact, the chance it targets a single company rather than one or more industries.
    public double CompanyScopeProbability { get; set; } = 0.5;

    public int MaxIndustriesPerPost { get; set; } = 3;
}
