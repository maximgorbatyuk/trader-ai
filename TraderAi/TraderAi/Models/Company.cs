namespace TraderAi.Models;

public sealed class Company
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public int IndustryId { get; set; }

    public int IssuedSharesCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
