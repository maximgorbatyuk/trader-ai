namespace TraderAi.Models;

// Cold storage for money transactions aged out of the live table. It is a plain column copy with no
// navigation so archiving is a bulk move; the running simulation never reads it, and it preserves the original Id.
public sealed class MoneyTransactionArchive
{
    public int Id { get; set; }

    public int? MarketRunId { get; set; }

    public int ParticipantId { get; set; }

    public MoneyTransactionType Type { get; set; }

    public decimal Amount { get; set; }

    public int? RelatedOrderId { get; set; }

    public int? RelatedShareTransactionId { get; set; }

    public int? RelatedLoanId { get; set; }

    public int? FromWhomId { get; set; }

    public string? Description { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
