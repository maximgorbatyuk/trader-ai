namespace TraderAi.Services;

public sealed class NewsOptions
{
    public const string SectionName = "News";

    // Opt-in: automated news stays off unless this is set to true. Manual posts are unaffected.
    public bool Enabled { get; set; }

    // Automated news is published once every this many completed cycles.
    public int CyclesBetweenPosts { get; set; } = 25;

    public int MaxIndustriesPerPost { get; set; } = 3;
}
