namespace TraderAi.Models;

public sealed class SettlementInstruction
{
    public int Id { get; set; }

    public int ShareTransactionId { get; set; }

    public ShareTransaction? ShareTransaction { get; set; }

    public int BuyerId { get; set; }

    public int? SellerId { get; set; }

    public int CompanyId { get; set; }

    public int Quantity { get; set; }

    public decimal CashAmount { get; set; }

    public decimal BuyerMarginAdvance { get; set; }

    public decimal SellerMarginInterestPayment { get; set; }

    public decimal SellerMarginDebitRepayment { get; set; }

    public int TradeDayNumber { get; set; }

    public int DueDayNumber { get; set; }

    public SettlementStatus Status { get; set; }

    public int CreatedInCycleId { get; set; }

    public int? SettledInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? SettledAt { get; set; }
}
