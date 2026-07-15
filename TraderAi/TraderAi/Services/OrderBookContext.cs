using TraderAi.Models;

namespace TraderAi.Services;

// The reloaded, lock-guarded market state one AI decision is applied against: the current market and cycle, the
// participant, and the batched latest-price and price-bound maps. Placement reuses MarketService's ordinary
// order path with these maps, so an AI order faces the same trading-break, LULD, price-range, buying-power,
// cash-reservation, conflicting-side, and owned-share checks as any other order.
public sealed class OrderBookContext
{
    public required Market Market { get; init; }

    public required int CurrentCycleId { get; init; }

    public required Participant Participant { get; init; }

    public required IReadOnlyDictionary<int, decimal> PriceByCompany { get; init; }

    public required IReadOnlyDictionary<int, OrderPriceBounds> BoundsByCompany { get; init; }

    public required decimal HoldingsValue { get; init; }

    public required decimal LoanLiability { get; init; }

    public required decimal MarginLiability { get; init; }
}

public sealed record AiOrderApplicationResult(
    int Index,
    OrderType Side,
    int CompanyId,
    int Quantity,
    decimal LimitPrice,
    string Reason,
    bool Applied,
    int? CreatedOrderId,
    string? RejectionReason);

public sealed record AiCancellationApplicationResult(
    int OrderId,
    bool Applied,
    string? RejectionReason);

public sealed record AiDecisionApplicationResult(
    bool ConfigurationStillCurrent,
    AiCancellationApplicationResult[] Cancellations,
    AiOrderApplicationResult[] Orders);
