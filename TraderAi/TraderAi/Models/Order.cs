using System.ComponentModel.DataAnnotations.Schema;

namespace TraderAi.Models;

public sealed class Order
{
    public int Id { get; set; }

    // Null for a company-originated sell order that lists the issuer's own shares.
    public int? ParticipantId { get; set; }

    public int CompanyId { get; set; }

    public OrderType Type { get; set; }

    public OrderStatus Status { get; set; }

    public int Quantity { get; set; }

    public int FilledQuantity { get; set; }

    public decimal LimitPrice { get; set; }

    public decimal ReservedCashAmount { get; set; }

    // Set on a loan-distress forced-sale order owned by LoanService; generic order ageing skips these the same
    // way it leaves a bankrupt trader's forced sales to their service owner.
    public int? RelatedLoanId { get; set; }

    public int? RelatedMarginCallId { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    [NotMapped]
    public int RemainingQuantity => Quantity - FilledQuantity;
}
