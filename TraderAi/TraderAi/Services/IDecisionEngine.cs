using TraderAi.Models;

namespace TraderAi.Services;

public sealed record CompanyQuote(int CompanyId, decimal Price);

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
