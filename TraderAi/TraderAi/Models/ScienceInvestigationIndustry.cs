namespace TraderAi.Models;

// Links a science investigation to one industry it lifts, with that industry's own increase so a single
// event can raise each sector by a different amount.
public sealed class ScienceInvestigationIndustry
{
    public int Id { get; set; }

    public int ScienceInvestigationId { get; set; }

    public int IndustryId { get; set; }

    public decimal ImpactPercent { get; set; }
}
