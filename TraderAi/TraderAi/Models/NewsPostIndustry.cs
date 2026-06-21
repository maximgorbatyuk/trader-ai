namespace TraderAi.Models;

// Links a news post to one industry it impacts; a post with industry scope has one row per industry.
public sealed class NewsPostIndustry
{
    public int Id { get; set; }

    public int NewsPostId { get; set; }

    public int IndustryId { get; set; }
}
