namespace TraderAi.Models;

// Append-only evidence of a split or reverse split. The before/after values make the denomination boundary
// directly auditable without rewriting trades that were valid under the preceding price scale.
public sealed class StockDenominationEvent
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public StockDenominationActionType ActionType { get; set; }

    public int Ratio { get; set; }

    public int IssuedSharesBefore { get; set; }

    public int IssuedSharesAfter { get; set; }

    public decimal PriceBefore { get; set; }

    public decimal PriceAfter { get; set; }

    public LuldState? LuldState { get; set; }

    public PriceLimitDirection? LimitDirection { get; set; }

    public decimal? ReferencePriceBefore { get; set; }

    public decimal? ReferencePriceAfter { get; set; }

    public decimal? LowerBandPriceBefore { get; set; }

    public decimal? LowerBandPriceAfter { get; set; }

    public decimal? UpperBandPriceBefore { get; set; }

    public decimal? UpperBandPriceAfter { get; set; }

    public int? LimitStateStartedCycleNumber { get; set; }

    public int? PauseUntilCycleNumber { get; set; }

    public int? PreviousPriceBandUpdatedInCycleId { get; set; }

    public int EffectiveInCycleId { get; set; }

    public int EffectiveInCycleNumber { get; set; }

    public DateTime CreatedAt { get; set; }
}
