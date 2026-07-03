namespace TraderAi.Models;

public sealed class Company
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public int IndustryId { get; set; }

    public int IssuedSharesCount { get; set; }

    // Capitalisation recorded at this company's most recent dividend window, the baseline the next window's
    // stability test compares against. Refreshed every window; null until the first one.
    public decimal? LastDividendCapitalization { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
