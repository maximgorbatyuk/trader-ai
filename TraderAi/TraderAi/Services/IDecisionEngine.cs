using TraderAi.Models;

namespace TraderAi.Services;

// Price signals the engine reads to bias trading: PriceChangePct is the move since the prior cycle's
// close, NetShareDemand is open participant buy shares minus sell shares, and LongRangeChangePct is the
// move versus roughly ten cycles ago (used for the extreme-move profit-take and buy-the-dip reactions), and
// SectorSentiment is the industry's current sentiment score for the sector-rotation signal. Bounds carries the
// active band and allowed range; issued supply, executable asks, and the batch block let automated Individuals
// bound buys without querying persistence, while their defaults preserve legacy callers.
public sealed record CompanyQuote(
    int CompanyId,
    decimal Price,
    decimal PriceChangePct = 0m,
    int NetShareDemand = 0,
    decimal LongRangeChangePct = 0m,
    int SectorSentiment = 0,
    OrderPriceBounds? Bounds = null,
    int IssuedShares = 0,
    decimal? BestExecutableSellPrice = null,
    int BestExecutableSellQuantity = 0,
    bool IndividualBuyBlockedForBatch = false);

// Everything a decision engine needs for one participant, supplied by the caller so the engine
// stays a pure function with no database access. CrisisActive is set while a market crisis window is open,
// which pulls conservative and low-risk traders back from buying. LoanLiability leans a trader toward selling;
// the optional financial fields let the automated-buy policy run while legacy contexts keep their old behavior.
public sealed record DecisionContext(
    Participant Participant,
    decimal AvailableCash,
    IReadOnlyList<CompanyQuote> Companies,
    IReadOnlyDictionary<int, int> SharesOwnedByCompany,
    IReadOnlySet<int> CompaniesWithOpenOrders,
    bool CrisisActive = false,
    decimal LoanLiability = 0m,
    decimal HoldingsValue = 0m,
    decimal NetWorth = 0m,
    decimal AvailableBalance = 0m,
    decimal BuyingPower = 0m,
    decimal MarginLiability = 0m,
    decimal ReservedBuyNotional = 0m,
    bool HasAutomatedTradingData = false);

public sealed record OrderIntent(OrderType Type, int CompanyId, int Quantity, decimal LimitPrice);

public interface IDecisionEngine
{
    IReadOnlyList<OrderIntent> Decide(DecisionContext context);
}
