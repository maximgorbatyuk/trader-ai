namespace TraderAi.Models;

public sealed class PriceBandState
{
    public int CompanyId { get; set; }

    public Company? Company { get; set; }

    public LuldState State { get; set; }

    public PriceLimitDirection? LimitDirection { get; set; }

    public decimal ReferencePrice { get; set; }

    public decimal LowerBandPrice { get; set; }

    public decimal UpperBandPrice { get; set; }

    public int? LimitStateStartedCycleNumber { get; set; }

    public int? PauseUntilCycleNumber { get; set; }

    public int UpdatedInCycleId { get; set; }

    // Matching only crosses orders inside the active band, so a forced order that must execute is pulled onto the
    // nearest band edge — up to the lower band, or down to the upper band. A band with no reference price yet
    // imposes no clamp.
    public decimal ClampToActiveBand(decimal price) =>
        ReferencePrice > 0m ? Math.Clamp(price, LowerBandPrice, UpperBandPrice) : price;
}
