namespace TraderAi.Models;

// The single price geometry a participant order is judged against, all derived from one LULD reference price.
// The active band is the executable window continuous matching uses; the wider allowed range is where an order
// may rest and wait for the band to reach it. Bounds are derived values, never persisted.
public sealed record OrderPriceBounds(
    decimal ReferencePrice,
    decimal ActiveLowerPrice,
    decimal ActiveUpperPrice,
    decimal AllowedMinimumPrice,
    decimal AllowedMaximumPrice)
{
    public bool IsWithinActiveBand(decimal price) =>
        price >= ActiveLowerPrice && price <= ActiveUpperPrice;

    public bool IsWithinAllowedRange(decimal price) =>
        price >= AllowedMinimumPrice && price <= AllowedMaximumPrice;

    // Pulls a price that would rest outside the executable band back onto the nearest band edge, so a forced
    // order that must cross cannot be stranded in the waiting range.
    public decimal ClampToActiveBand(decimal price) =>
        Math.Clamp(price, ActiveLowerPrice, ActiveUpperPrice);

    public static OrderPriceBounds FromReference(
        decimal referencePrice,
        decimal lowerBandPercent,
        decimal upperBandPercent,
        decimal allowedLowerPercent,
        decimal allowedUpperPercent) =>
        new(
            Round(referencePrice),
            Round(referencePrice * (1m - (lowerBandPercent / 100m))),
            Round(referencePrice * (1m + (upperBandPercent / 100m))),
            Round(referencePrice * (1m - (allowedLowerPercent / 100m))),
            Round(referencePrice * (1m + (allowedUpperPercent / 100m))));

    // Resolves bounds for a company from its persisted band when one exists, otherwise from the latest price as a
    // temporary reference; the active band reuses the persisted edges so entry and matching read one window.
    // Returns null when no positive reference is available, so callers skip a company that cannot be priced.
    public static OrderPriceBounds? Resolve(
        PriceBandState? band,
        decimal latestPrice,
        decimal lowerBandPercent,
        decimal upperBandPercent,
        decimal allowedLowerPercent,
        decimal allowedUpperPercent)
    {
        var hasBand = band is { ReferencePrice: > 0m };
        var reference = hasBand ? band!.ReferencePrice : latestPrice;
        if (reference <= 0m)
        {
            return null;
        }

        return new OrderPriceBounds(
            Round(reference),
            hasBand ? band!.LowerBandPrice : Round(reference * (1m - (lowerBandPercent / 100m))),
            hasBand ? band!.UpperBandPrice : Round(reference * (1m + (upperBandPercent / 100m))),
            Round(reference * (1m - (allowedLowerPercent / 100m))),
            Round(reference * (1m + (allowedUpperPercent / 100m))));
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
