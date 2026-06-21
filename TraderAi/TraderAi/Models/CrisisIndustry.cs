namespace TraderAi.Models;

// Links a crisis to one industry it drives down, with that industry's own decrease so a single crisis can
// hit each sector by a different amount.
public sealed class CrisisIndustry
{
    public int Id { get; set; }

    public int CrisisId { get; set; }

    public int IndustryId { get; set; }

    public decimal ImpactPercent { get; set; }
}
