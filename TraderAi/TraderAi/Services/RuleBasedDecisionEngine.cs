using Microsoft.Extensions.Options;
using TraderAi.Models;

namespace TraderAi.Services;

// Baseline trader: each tick it makes one global choice — buy, sell, or do nothing. Recent price moves
// and order-book demand bias that choice toward buying risers or selling fallers, with stronger
// reactions at extreme long-range moves, and otherwise the choice is a uniform pick among the open
// actions. Order size comes from the injected sizer, buyers bid 1–5% above and sellers ask 1–5% below
// the last price so orders cross, and an LLM-backed engine can later implement the same interface.
public sealed class RuleBasedDecisionEngine(
    ITradeSizer tradeSizer,
    IOptions<RandomChanceRatesOptions> chanceRates,
    Random random) : IDecisionEngine
{
    // Recent (one-cycle) move maps to a buy/sell pull, ramping with the size of the move up to a cap; a
    // ~5% move alone reaches the cap.
    private const double RecentMoveScale = 4.0;
    private const double RecentMoveCap = 0.20;

    // Net buy demand on the resting book adds a buy pull; ten net shares add one percentage point.
    private const double ImbalanceBiasPerShare = 0.001;
    private const double MaxImbalanceBias = 0.20;

    // Beyond a ~60% move versus roughly ten cycles ago, holders take profit on a run-up and bystanders
    // hunt the bargain on a deep drop.
    private const decimal ExtremeMoveThreshold = 0.60m;
    private const double ProfitTakingSellBias = 0.40;
    private const double BargainBuyBias = 0.40;

    // Sector rotation and fund flows direct capital toward favoured industries and away from shunned ones.
    private const double SentimentBuyWeight = 0.20;
    private const double SentimentSellWeight = 0.20;

    // Caps keep any single side from saturating the random draw once several pulls stack.
    private const double MaxBuyPull = 0.80;
    private const double MaxSellPull = 0.80;

    // Risk profile scales the signal pulls: a high-risk trader reacts harder to price moves, a low-risk
    // one holds back. Medium leaves the pull untouched so its behaviour matches the signal alone.
    private const double HighRiskPullFactor = 1.5;
    private const double LowRiskPullFactor = 0.6;

    // A trader carrying loan debt wants to sell to repay it: each percentage point of loan liability against
    // total worth adds this much sell pull, clamped at the 40% borrow ceiling. Cautious traders deleverage
    // hardest, gamblers least.
    private const double LowRiskDebtSellPerPercent = 0.0075;
    private const double MediumRiskDebtSellPerPercent = 0.005;
    private const double HighRiskDebtSellPerPercent = 0.0025;
    private const decimal MaxDebtPercent = 40m;

    private const decimal MinPriceOffset = 0.01m;
    private const decimal MaxPriceOffset = 0.05m;

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

        var (buyTarget, buyPull) = StrongestBuy(context, buyCandidates);
        var (sellTarget, sellPull) = StrongestSell(sellCandidates);

        var riskFactor = RiskPullFactor(context.Participant.RiskProfile);
        buyPull = Math.Min(MaxBuyPull, buyPull * riskFactor);
        sellPull *= riskFactor;

        // Debt leans the trader toward selling to repay; the pull scales with how deep the debt runs against
        // its worth and with risk appetite, and it needs some holding worth selling before it applies.
        var debtSellPull = DebtSellPull(context, sellCandidates);
        if (debtSellPull > 0.0 && sellTarget is null)
        {
            sellTarget = MostValuableHolding(context, sellCandidates);
        }

        sellPull = Math.Min(MaxSellPull, sellPull + debtSellPull);

        // One draw splits into a buy band [0, buyPull) and a sell band [buyPull, buyPull + sellPull);
        // anything above is left to the uniform fallback. A pulled action that cannot be built (no cash,
        // nothing to sell) also falls through rather than spilling into the other band.
        var roll = random.NextDouble();
        if (buyTarget is not null && roll < buyPull)
        {
            if (BuildBuy(context, buyTarget) is { } pulledBuy)
            {
                return Gate(pulledBuy, context);
            }
        }
        else if (sellTarget is not null && roll < buyPull + sellPull)
        {
            if (BuildSell(context, sellTarget) is { } pulledSell)
            {
                return Gate(pulledSell, context);
            }
        }

        // The uniform fallback is where the personality frequency nudge lives: low-risk and conservative
        // traders get extra weight on doing nothing, high-risk and aggressive ones get extra weight on
        // acting. A balanced/medium trader keeps a single copy of each, matching the original behaviour.
        var skipWeight = SkipWeight(context.Participant);
        var actionWeight = ActionWeight(context.Participant);

        var actions = new List<TradeAction>();
        for (var copy = 0; copy < skipWeight; copy++)
        {
            actions.Add(TradeAction.Skip);
        }

        if (sellCandidates.Count > 0)
        {
            for (var copy = 0; copy < actionWeight; copy++)
            {
                actions.Add(TradeAction.Sell);
            }
        }

        if (buyCandidates.Count > 0)
        {
            for (var copy = 0; copy < actionWeight; copy++)
            {
                actions.Add(TradeAction.Buy);
            }
        }

        var intent = actions[random.Next(actions.Count)] switch
        {
            TradeAction.Sell => BuildSell(context, sellCandidates[random.Next(sellCandidates.Count)]),
            TradeAction.Buy => BuildBuy(context, buyCandidates[random.Next(buyCandidates.Count)]),
            _ => null,
        };

        return intent is null ? [] : Gate(intent, context);
    }

    // During a crisis, a conservative or low-risk trader's buy is dropped to nothing with the suppression
    // chance (traits stack); a suppressed buy becomes inaction rather than falling through to another order.
    // The draw is taken only when a suppression actually applies, so calm-market decisions are unchanged.
    private IReadOnlyList<OrderIntent> Gate(OrderIntent intent, DecisionContext context)
    {
        if (intent.Type != OrderType.Buy)
        {
            return [intent];
        }

        var keep = CrisisBuyKeepProbability(context.Participant, context.CrisisActive);
        if (keep < 1.0 && random.NextDouble() >= keep)
        {
            return [];
        }

        return [intent];
    }

    // While a crisis window is open, a buy is dropped by CrisisBuySuppression per matching trait: conservative
    // temperament and low-risk profile each apply it, and a trader with both has them stack (≈28% fewer buys).
    private double CrisisBuyKeepProbability(Participant participant, bool crisisActive)
    {
        if (!crisisActive)
        {
            return 1.0;
        }

        var suppression = chanceRates.Value.ChanceModifiers.CrisisBuySuppression;
        var keep = 1.0;
        if (participant.Temperament == Temperament.Conservative)
        {
            keep *= 1.0 - suppression;
        }

        if (participant.RiskProfile == RiskProfile.Low)
        {
            keep *= 1.0 - suppression;
        }

        return keep;
    }

    private static (CompanyQuote? Target, double Pull) StrongestBuy(
        DecisionContext context,
        IReadOnlyList<CompanyQuote> candidates)
    {
        CompanyQuote? target = null;
        var best = 0.0;

        foreach (var quote in candidates)
        {
            var owns = context.SharesOwnedByCompany.GetValueOrDefault(quote.CompanyId) > 0;
            var pull = BuyPull(quote, owns);
            if (pull > best)
            {
                best = pull;
                target = quote;
            }
        }

        return (target, best);
    }

    private static (CompanyQuote? Target, double Pull) StrongestSell(IReadOnlyList<CompanyQuote> candidates)
    {
        CompanyQuote? target = null;
        var best = 0.0;

        foreach (var quote in candidates)
        {
            var pull = SellPull(quote);
            if (pull > best)
            {
                best = pull;
                target = quote;
            }
        }

        return (target, best);
    }

    private static double BuyPull(CompanyQuote quote, bool owns)
    {
        // A rising price draws buyers in, the harder the faster it rose this cycle.
        var growth = RecentMovePull(quote.PriceChangePct);

        // More resting buy demand than sell supply signals upward pressure worth front-running.
        var imbalance = quote.NetShareDemand > 0
            ? Math.Min(MaxImbalanceBias, quote.NetShareDemand * ImbalanceBiasPerShare)
            : 0.0;

        // A deep, sustained drop tempts bargain hunters who do not already hold the share.
        var bargain = !owns && quote.LongRangeChangePct <= -ExtremeMoveThreshold ? BargainBuyBias : 0.0;

        var sentiment = Math.Clamp((double)quote.SectorSentiment / 1000.0, 0.0, 1.0) * SentimentBuyWeight;

        return Math.Min(MaxBuyPull, growth + imbalance + bargain + sentiment);
    }

    private static double SellPull(CompanyQuote quote)
    {
        // A falling price makes holders want out, the harder the faster it fell this cycle.
        var decline = RecentMovePull(-quote.PriceChangePct);

        // A large run-up makes holders lock in profit before it reverses.
        var profitTaking = quote.LongRangeChangePct >= ExtremeMoveThreshold ? ProfitTakingSellBias : 0.0;

        var sentiment = Math.Clamp(-(double)quote.SectorSentiment / 1000.0, 0.0, 1.0) * SentimentSellWeight;

        return Math.Min(MaxSellPull, decline + profitTaking + sentiment);
    }

    // Maps a one-cycle move in the favourable direction (positive only) to a capped pull.
    private static double RecentMovePull(decimal favourableChangePct) =>
        favourableChangePct > 0m
            ? Math.Min(RecentMoveCap, (double)favourableChangePct * RecentMoveScale)
            : 0.0;

    private static double RiskPullFactor(RiskProfile riskProfile) => riskProfile switch
    {
        RiskProfile.High => HighRiskPullFactor,
        RiskProfile.Low => LowRiskPullFactor,
        _ => 1.0,
    };

    private static double DebtSellPull(DecisionContext context, IReadOnlyList<CompanyQuote> sellCandidates)
    {
        if (sellCandidates.Count == 0 || context.LoanLiability <= 0m)
        {
            return 0.0;
        }

        var debt = context.LoanLiability;
        var worth = context.Participant.CurrentBalance + HoldingsValue(context);
        var debtPercent = worth > 0m
            ? Math.Min(debt / worth * 100m, MaxDebtPercent)
            : MaxDebtPercent;

        return (double)debtPercent * DebtSellRate(context.Participant.RiskProfile);
    }

    private static double DebtSellRate(RiskProfile riskProfile) => riskProfile switch
    {
        RiskProfile.High => HighRiskDebtSellPerPercent,
        RiskProfile.Low => LowRiskDebtSellPerPercent,
        _ => MediumRiskDebtSellPerPercent,
    };

    private static decimal HoldingsValue(DecisionContext context) =>
        context.Companies.Sum(quote =>
            context.SharesOwnedByCompany.GetValueOrDefault(quote.CompanyId) * quote.Price);

    // The holding whose shares are worth the most raises the most cash toward the debt, so debt-driven selling
    // targets it when no price signal already points somewhere.
    private static CompanyQuote? MostValuableHolding(DecisionContext context, IReadOnlyList<CompanyQuote> sellCandidates)
    {
        CompanyQuote? best = null;
        var bestValue = 0m;
        foreach (var quote in sellCandidates)
        {
            var value = context.SharesOwnedByCompany.GetValueOrDefault(quote.CompanyId) * quote.Price;
            if (value > bestValue)
            {
                bestValue = value;
                best = quote;
            }
        }

        return best;
    }

    private static int SkipWeight(Participant participant) =>
        1 + (participant.RiskProfile == RiskProfile.Low ? 1 : 0)
          + (participant.Temperament == Temperament.Conservative ? 1 : 0);

    private static int ActionWeight(Participant participant) =>
        1 + (participant.RiskProfile == RiskProfile.High ? 1 : 0)
          + (participant.Temperament == Temperament.Aggressive ? 1 : 0);

    private OrderIntent? BuildSell(DecisionContext context, CompanyQuote quote)
    {
        var sharesOwned = context.SharesOwnedByCompany.GetValueOrDefault(quote.CompanyId);

        var quantity = tradeSizer.Size(context.Participant.Temperament, sharesOwned);
        if (quantity < 1)
        {
            return null;
        }

        var sellLimit = Round(quote.Price * (1m - RandomOffset()));
        return sellLimit > 0m
            ? new OrderIntent(OrderType.Sell, quote.CompanyId, quantity, sellLimit)
            : null;
    }

    private OrderIntent? BuildBuy(DecisionContext context, CompanyQuote quote)
    {
        var buyLimit = Round(quote.Price * (1m + RandomOffset()));
        if (buyLimit <= 0m)
        {
            return null;
        }

        // Cash divided by a low post-split price can exceed the 32-bit order-quantity field; clamp before
        // the checked decimal-to-int cast so an affordable count past the limit cannot overflow.
        var maxAffordable = (int)Math.Clamp(Math.Floor(context.AvailableCash / buyLimit), 0m, int.MaxValue);
        var quantity = tradeSizer.Size(context.Participant.Temperament, maxAffordable);
        return quantity >= 1
            ? new OrderIntent(OrderType.Buy, quote.CompanyId, quantity, buyLimit)
            : null;
    }

    private decimal RandomOffset() =>
        MinPriceOffset + ((decimal)random.NextDouble() * (MaxPriceOffset - MinPriceOffset));

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
