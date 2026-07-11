namespace TraderAi.Models;

// Per-company breakdown behind a single Dividend money transaction: one row per company that paid into that
// payout, so the cash-movement detail view can name the payers and their amounts. It lives only while its parent
// transaction stays in the live table and is dropped alongside it when that transaction ages into the archive.
public sealed class DividendPayout
{
    public int Id { get; set; }

    public int MoneyTransactionId { get; set; }

    public MoneyTransaction? MoneyTransaction { get; set; }

    public int CompanyId { get; set; }

    public decimal Amount { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
