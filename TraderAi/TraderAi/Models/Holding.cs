namespace TraderAi.Models;

// A participant's position in one company: how many shares it holds and the weighted-average price
// it paid for them. Replaces per-share rows, so a holding of N shares is one row, not N. The issuer's
// unsold float is not a holding — it is IssuedSharesCount minus the sum of participant holdings.
public sealed class Holding
{
    public int Id { get; set; }

    public int ParticipantId { get; set; }

    public int CompanyId { get; set; }

    public int Quantity { get; set; }

    public int SettledQuantity { get; set; }

    // Weighted-average cost basis per share; a buy fill blends the fill price in, a sell leaves it
    // unchanged, and a split divides it. CostBasis for display is Quantity times this.
    public decimal AverageCost { get; set; }
}
