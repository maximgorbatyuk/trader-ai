namespace TraderAi.Models;

// A record of a company issuing new free shares and handing them to non-holders. A large company dilutes its
// per-share price this way; the row keeps the figures so the event can be shown on the company page.
public sealed class ShareEmission
{
    public int Id { get; set; }

    public int? MarketRunId { get; set; }

    public int CompanyId { get; set; }

    public int SharesEmitted { get; set; }

    public int RecipientCount { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
