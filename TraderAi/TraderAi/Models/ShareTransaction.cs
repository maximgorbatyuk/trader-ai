namespace TraderAi.Models;

public sealed class ShareTransaction
{
    public int Id { get; set; }

    // Null when the seller is the issuing company rather than a participant.
    public int? SellerId { get; set; }

    public int BuyerId { get; set; }

    public int CompanyId { get; set; }

    public int Quantity { get; set; }

    public decimal Price { get; set; }

    public decimal TotalCost { get; set; }

    public decimal? SellerAverageCost { get; set; }

    public decimal? SellerCostBasis { get; set; }

    public decimal? SellerTradeFee { get; set; }

    public decimal? SellerManagerFee { get; set; }

    public decimal? SellerGrossRealizedPnl { get; set; }

    public decimal? SellerNetRealizedPnl { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public SettlementInstruction? SettlementInstruction { get; set; }
}
