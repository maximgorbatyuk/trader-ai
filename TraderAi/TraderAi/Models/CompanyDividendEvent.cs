namespace TraderAi.Models;

public sealed class CompanyDividendEvent
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public decimal DeclaredAmount { get; set; }

    public decimal FundedAmount { get; set; }

    public DividendFundingOutcome FundingOutcome { get; set; }

    public decimal IssuerCashBeforeFunding { get; set; }

    public int CreatedInCycleId { get; set; }

    public int TradingDayNumber { get; set; }

    public DateTime CreatedAt { get; set; }
}
