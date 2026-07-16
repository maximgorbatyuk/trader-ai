namespace TraderAi.Models;

// A record of a big-investment deal: a participant or fund funded a company by buying a block of newly minted
// shares at the current price. The row keeps the figures so the deal can be shown on the company, participant,
// and market pages. Ids are plain scalars with no navigation so the record survives a departed investor or a
// closed company, like ShareEmission.
public sealed class CompanyInvestment
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public int InvestorParticipantId { get; set; }

    public decimal DealValue { get; set; }

    public int SharesIssued { get; set; }

    public int SharesBeforeDeal { get; set; }

    public decimal CapitalizationBeforeDeal { get; set; }

    // Deal price times the enlarged issued-share count, so it reflects the deal itself before the auditor raise.
    public decimal FinalCapitalization { get; set; }

    // The investor's total stake after the deal as a percent (0–100): prior holding plus issued shares over the
    // enlarged issued-share count. Stored as a percent so the money-scale decimal precision keeps two decimals.
    public decimal InvestorSharePercent { get; set; }

    // Trading day the deal settled; null only if the cycle has no resolvable trading day.
    public int? TradingDayNumber { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
