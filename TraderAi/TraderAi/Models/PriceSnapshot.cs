namespace TraderAi.Models;

public sealed class PriceSnapshot
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public decimal Price { get; set; }

    public int? SourceShareTransactionId { get; set; }

    public ShareTransaction? SourceShareTransaction { get; set; }

    public int CreatedInCycleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
