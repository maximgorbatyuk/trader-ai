namespace TraderAi.Models;

// Append-only evidence for every issuer cash change. Amounts are positive magnitudes; the type determines
// whether the movement credits or debits the company's current cash balance.
public sealed class CorporateCashTransaction
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public CorporateCashTransactionType Type { get; set; }

    public decimal Amount { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
