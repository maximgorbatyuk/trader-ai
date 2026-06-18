namespace TraderAi.Models;

public sealed class OrderFill
{
    public int Id { get; set; }

    public int BuyOrderId { get; set; }

    public int SellOrderId { get; set; }

    public int Quantity { get; set; }

    public decimal ExecutionPrice { get; set; }

    public int CreatedInCycleId { get; set; }

    public int ShareTransactionId { get; set; }

    public ShareTransaction? ShareTransaction { get; set; }

    public DateTime CreatedAt { get; set; }
}
