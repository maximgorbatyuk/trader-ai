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

    // Matching skips any order resting below the active lower band, so a forced sale that prices under it would
    // never execute; this floors such an ask at the band. A band with no reference price yet imposes no floor.
    public decimal FloorSellPrice(decimal price) =>
        ReferencePrice > 0m && price < LowerBandPrice ? LowerBandPrice : price;
}
