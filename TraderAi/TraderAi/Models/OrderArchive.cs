namespace TraderAi.Models;

// Cold storage for terminal orders aged out of the live book. It is a plain column copy with no navigation
// so archiving is a bulk move; the running simulation never reads it, and it preserves the original Id so
// historical money-transaction and share-transaction references still resolve.
public sealed class OrderArchive
{
    public int Id { get; set; }

    public int? MarketRunId { get; set; }

    public int? ParticipantId { get; set; }

    public int CompanyId { get; set; }

    public OrderType Type { get; set; }

    public OrderStatus Status { get; set; }

    public int Quantity { get; set; }

    public int FilledQuantity { get; set; }

    public decimal LimitPrice { get; set; }

    public decimal ReservedCashAmount { get; set; }

    public bool IsFloatReplenishment { get; set; }

    public int? RelatedLoanId { get; set; }

    public int? RelatedMarginCallId { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
