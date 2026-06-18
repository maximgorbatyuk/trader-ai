namespace TraderAi.Models;

public sealed class MoneyTransaction
{
    public int Id { get; set; }

    public int ParticipantId { get; set; }

    public MoneyTransactionType Type { get; set; }

    public decimal Amount { get; set; }

    public int? RelatedOrderId { get; set; }

    public Order? RelatedOrder { get; set; }

    public int? RelatedShareTransactionId { get; set; }

    public ShareTransaction? RelatedShareTransaction { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
