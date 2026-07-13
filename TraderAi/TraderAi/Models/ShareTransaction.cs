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

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public SettlementInstruction? SettlementInstruction { get; set; }
}
