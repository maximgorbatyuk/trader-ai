namespace TraderAi.Models;

// Append-only evidence separates newly minted supply from relisted issuer float, which uses the same order type
// but does not change the company's issued-share count.
public sealed class PrimaryIssuanceEvent
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public int CreatedInCycleId { get; set; }

    public int IssuedSharesBefore { get; set; }

    public int NewlyIssuedShares { get; set; }

    public int IssuedSharesAfter { get; set; }

    public DateTime CreatedAt { get; set; }
}
