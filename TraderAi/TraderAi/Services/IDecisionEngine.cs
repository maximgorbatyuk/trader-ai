using TraderAi.Models;

namespace TraderAi.Services;

// Price signals the engine reads to bias trading: PriceChangePct is the move since the prior cycle's
// close, NetShareDemand is open participant buy shares minus sell shares, and LongRangeChangePct is the
// move versus roughly ten cycles ago (used for the extreme-move profit-take and buy-the-dip reactions).
public sealed record CompanyQuote(
    int CompanyId,
    decimal Price,
    decimal PriceChangePct = 0m,
    int NetShareDemand = 0,
    decimal LongRangeChangePct = 0m);

// Everything a decision engine needs for one participant, supplied by the caller so the engine
// stays a pure function with no database access.
public sealed record DecisionContext(
    Participant Participant,
    decimal AvailableCash,
    IReadOnlyList<CompanyQuote> Companies,
    IReadOnlyDictionary<int, int> SharesOwnedByCompany,
    IReadOnlySet<int> CompaniesWithOpenOrders);

public sealed record OrderIntent(OrderType Type, int CompanyId, int Quantity, decimal LimitPrice);

public interface IDecisionEngine
{
    IReadOnlyList<OrderIntent> Decide(DecisionContext context);
}
