using TraderAi.Models;

namespace TraderAi.Services;

// Baseline trader: each tick it makes a single global choice — sell, buy, or do nothing — picked at
// random among the actions currently open to the participant. Order size comes from the injected
// sizer; temperament still sets how far the limit price crosses the market. An LLM-backed engine can
// later implement the same interface.
public sealed class RuleBasedDecisionEngine(ITradeSizer tradeSizer, Random random) : IDecisionEngine
{
    private enum TradeAction
    {
        Skip,
        Sell,
        Buy,
    }

    public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
    {
        // A company with an open order is excluded so the participant never stacks duplicate intents.
        var sellCandidates = context.Companies
            .Where(quote => !context.CompaniesWithOpenOrders.Contains(quote.CompanyId)
                && context.SharesOwnedByCompany.GetValueOrDefault(quote.CompanyId) > 0)
            .ToList();

        var buyCandidates = context.AvailableCash > 0m
            ? context.Companies
                .Where(quote => !context.CompaniesWithOpenOrders.Contains(quote.CompanyId))
                .ToList()
            : [];

        var actions = new List<TradeAction> { TradeAction.Skip };
        if (sellCandidates.Count > 0)
        {
            actions.Add(TradeAction.Sell);
        }

        if (buyCandidates.Count > 0)
        {
            actions.Add(TradeAction.Buy);
        }

        var intent = actions[random.Next(actions.Count)] switch
        {
            TradeAction.Sell => BuildSell(context, sellCandidates),
            TradeAction.Buy => BuildBuy(context, buyCandidates),
            _ => null,
        };

        return intent is null ? [] : [intent];
    }

    private OrderIntent? BuildSell(DecisionContext context, IReadOnlyList<CompanyQuote> candidates)
    {
        var quote = candidates[random.Next(candidates.Count)];
        var sharesOwned = context.SharesOwnedByCompany.GetValueOrDefault(quote.CompanyId);

        var quantity = tradeSizer.Size(context.Participant.Temperament, sharesOwned);
        if (quantity < 1)
        {
            return null;
        }

        var sellLimit = Round(quote.Price * SellMultiplier(context.Participant.Temperament));
        return sellLimit > 0m
            ? new OrderIntent(OrderType.Sell, quote.CompanyId, quantity, sellLimit)
            : null;
    }

    private OrderIntent? BuildBuy(DecisionContext context, IReadOnlyList<CompanyQuote> candidates)
    {
        var quote = candidates[random.Next(candidates.Count)];
        var buyLimit = Round(quote.Price * BuyMultiplier(context.Participant.Temperament));
        if (buyLimit <= 0m)
        {
            return null;
        }

        var maxAffordable = (int)Math.Floor(context.AvailableCash / buyLimit);
        var quantity = tradeSizer.Size(context.Participant.Temperament, maxAffordable);
        return quantity >= 1
            ? new OrderIntent(OrderType.Buy, quote.CompanyId, quantity, buyLimit)
            : null;
    }

    private static decimal BuyMultiplier(Temperament temperament) => temperament switch
    {
        Temperament.Aggressive => 1.10m,
        Temperament.Balanced => 1.02m,
        Temperament.Conservative => 0.90m,
        _ => 1.00m,
    };

    private static decimal SellMultiplier(Temperament temperament) => temperament switch
    {
        Temperament.Aggressive => 0.90m,
        Temperament.Balanced => 0.98m,
        Temperament.Conservative => 1.10m,
        _ => 1.00m,
    };

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
