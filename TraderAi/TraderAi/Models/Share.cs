namespace TraderAi.Models;

public sealed class Share
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    // Null while the share is still held by its issuing company and not owned by any participant.
    public int? OwnerId { get; set; }

    public decimal InitialPrice { get; set; }

    public decimal CurrentPrice { get; set; }

    public DateTime LastUpdatedAt { get; set; }

    public int? LastShareTransactionId { get; set; }

    public ShareTransaction? LastShareTransaction { get; set; }
}
