using Microsoft.Extensions.Options;
using TraderAi.Models;

namespace TraderAi.Services;

// Baseline trader: each tick it makes one global choice — buy, sell, or do nothing. Recent price moves
// and order-book demand bias that choice toward buying risers or selling fallers, with stronger
// reactions at extreme long-range moves, and otherwise the choice is a uniform pick among the open
// actions. Legacy orders keep their randomized pricing; enriched Individuals cross the best available ask, or
// place a bounded above-market passive bid only on a rising-or-stable company with no resting seller. Rule-based
// sells spread symmetrically around the reference, and an LLM-backed engine can implement the same pure interface.
public sealed class RuleBasedDecisionEngine(
    ITradeSizer tradeSizer,
    IOptions<RandomChanceRatesOptions> chanceRates,
    Random random,
    AutomatedBuyOrderPolicy? automatedBuyOrderPolicy = null,
    IOptions<AutomatedTradingOptions>? automatedTradingOptions = null) : IDecisionEngine
{
    private readonly IOptions<AutomatedTradingOptions> automatedTradingOptions = automatedTradingOptions
        ?? Options.Create(new AutomatedTradingOptions());
    private readonly AutomatedBuyOrderPolicy automatedBuyOrderPolicy = automatedBuyOrderPolicy
        ?? new AutomatedBuyOrderPolicy(automatedTradingOptions ?? Options.Create(new AutomatedTradingOptions()));

    // Recent (one-cycle) move maps to a buy/sell pull, ramping with the size of the move up to a cap; a
    // ~5% move alone reaches the cap.
    private const double RecentMoveScale = 4.0;
    private const double RecentMoveCap = 0.20;

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
    private const double BelowTargetBuyPull = 0.60;

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
        // A company with an open order is excluded so the participant never stacks duplicate intents; one with no
        // resolvable price bounds is skipped entirely because a valid limit cannot be placed for it.
        var sellCandidates = context.Companies
            .Where(quote => quote.Bounds is not null
                && !context.CompaniesWithOpenOrders.Contains(quote.CompanyId)
                && context.SharesOwnedByCompany.GetValueOrDefault(quote.CompanyId) > 0)
            .ToList();

        var automatedExposure = AutomatedExposure(context);
        var automatedBuyBlocked = automatedExposure is not null
            && automatedExposure.CurrentExposurePercent >= automatedExposure.Target.MaximumExposurePercent;
        var usesAutomatedBuyPolicy = UsesAutomatedBuyPolicy(context);
        var buyCandidates = context.AvailableCash > 0m && !automatedBuyBlocked
            ? context.Companies
                .Where(quote => quote.Bounds is not null
                    && !context.CompaniesWithOpenOrders.Contains(quote.CompanyId)
                    && !(usesAutomatedBuyPolicy && quote.IndividualBuyBlockedForBatch)
                    && (!usesAutomatedBuyPolicy
                        || HasExecutableAsk(quote)
                        || (quote.OpenSellQuantity == 0 && IsRisingOrStable(quote))))
                .ToList()
            : [];
        var actionableBuyCandidates = buyCandidates;
        if (automatedExposure?.Position == AutomatedExposurePosition.Below)
        {
            var executableCandidates = buyCandidates.Where(HasExecutableAsk).ToList();
            if (executableCandidates.Count > 0)
            {
                actionableBuyCandidates = executableCandidates;
            }
        }

        var (buyTarget, buyPull) = StrongestBuy(context, actionableBuyCandidates);
        var (sellTarget, sellPull) = StrongestSell(sellCandidates);

        var riskFactor = RiskPullFactor(context.Participant.RiskProfile);
        buyPull = Math.Min(MaxBuyPull, buyPull * riskFactor);
        sellPull *= riskFactor;

        if (automatedExposure?.Position == AutomatedExposurePosition.Below && actionableBuyCandidates.Count > 0)
        {
            // A signal fixes the target without another draw; only the no-signal fallback diversifies it.
            buyTarget ??= actionableBuyCandidates[random.Next(actionableBuyCandidates.Count)];
            buyPull = Math.Max(buyPull, BelowTargetBuyPull);
        }

        if (usesAutomatedBuyPolicy
            && buyTarget is null
            && actionableBuyCandidates.Count > 0
            && actionableBuyCandidates.All(quote => !HasExecutableAsk(quote)))
        {
            buyTarget = actionableBuyCandidates[random.Next(actionableBuyCandidates.Count)];
        }

        if (buyTarget is not null && IsAutomatedPassiveBuy(context, buyTarget))
        {
            if (BuildAllowedBuy(context, buyTarget) is { } passiveBuy)
            {
                return Resolve(passiveBuy, context, actionableBuyCandidates);
            }

            actionableBuyCandidates = actionableBuyCandidates
                .Where(HasExecutableAsk)
                .ToList();
            if (actionableBuyCandidates.Count == 0 && sellCandidates.Count == 0)
            {
                return [];
            }

            buyTarget = null;
            buyPull = 0.0;
        }

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
                return Resolve(pulledBuy, context, actionableBuyCandidates);
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

        if (actionableBuyCandidates.Count > 0)
        {
            for (var copy = 0; copy < actionWeight; copy++)
            {
                actions.Add(TradeAction.Buy);
            }
        }

        var intent = actions[random.Next(actions.Count)] switch
        {
            TradeAction.Sell => BuildSell(context, sellCandidates[random.Next(sellCandidates.Count)]),
            TradeAction.Buy => BuildAllowedBuy(
                context,
                actionableBuyCandidates[random.Next(actionableBuyCandidates.Count)]),
            _ => null,
        };

        return intent is null ? [] : Resolve(intent, context, actionableBuyCandidates);
    }

    // A decided sell (or a crisis-suppressed buy) is returned unchanged; a surviving buy fans out into a random
    // cluster of distinct buys so a buying cycle can add real upward pressure rather than a single order.
    private IReadOnlyList<OrderIntent> Resolve(
        OrderIntent intent,
        DecisionContext context,
        IReadOnlyList<CompanyQuote> buyCandidates)
    {
        var gated = Gate(intent, context);
        return gated.Count != 0 && intent.Type == OrderType.Buy
            ? FanOutBuys(context, intent, buyCandidates)
            : gated;
    }

    // The count is drawn once in [BuyOrdersPerCycleMin, BuyOrdersPerCycleMax]; a min equal to max is deterministic
    // and consumes no draw so the single-order default leaves existing decision sequences untouched. Extra buys
    // target the next strongest eligible companies and reuse the ordinary build so their pricing and gating hold.
    private IReadOnlyList<OrderIntent> FanOutBuys(
        DecisionContext context,
        OrderIntent firstBuy,
        IReadOnlyList<CompanyQuote> buyCandidates)
    {
        var options = automatedTradingOptions.Value;
        var minimum = Math.Max(0, options.BuyOrdersPerCycleMin);
        var maximum = Math.Max(minimum, options.BuyOrdersPerCycleMax);
        var count = minimum == maximum ? minimum : random.Next(minimum, maximum + 1);
        if (count <= 0)
        {
            return [];
        }

        var buys = new List<OrderIntent>(count) { firstBuy };
        if (count == 1)
        {
            return buys;
        }

        var used = new HashSet<int> { firstBuy.CompanyId };
        foreach (var quote in buyCandidates.OrderByDescending(candidate =>
            BuyPull(candidate, context.SharesOwnedByCompany.GetValueOrDefault(candidate.CompanyId) > 0)))
        {
            if (buys.Count >= count)
            {
                break;
            }

            if (!used.Add(quote.CompanyId))
            {
                continue;
            }

            if (BuildAllowedBuy(context, quote) is { } extraBuy)
            {
                buys.Add(extraBuy);
            }
        }

        return buys;
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
        var imbalance = quote.OrderFlowImbalance > 0m
            ? Math.Min(MaxImbalanceBias, (double)quote.OrderFlowImbalance * MaxImbalanceBias)
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

        var sellLimit = PickLimitPrice(quote, OrderType.Sell);
        return sellLimit > 0m
            ? new OrderIntent(OrderType.Sell, quote.CompanyId, quantity, sellLimit)
            : null;
    }

    private OrderIntent? BuildBuy(DecisionContext context, CompanyQuote quote)
    {
        if (UsesAutomatedBuyPolicy(context))
        {
            return BuildAutomatedIndividualBuy(context, quote);
        }

        var buyLimit = PickLimitPrice(quote, OrderType.Buy);
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

    private OrderIntent? BuildAllowedBuy(DecisionContext context, CompanyQuote quote)
    {
        if (IsAutomatedPassiveBuy(context, quote)
            && random.NextDouble() >= chanceRates.Value.EventTriggerChances.NoSellOrderBuyChance)
        {
            return null;
        }

        return BuildBuy(context, quote);
    }

    private OrderIntent? BuildAutomatedIndividualBuy(DecisionContext context, CompanyQuote quote)
    {
        var bestAsk = quote.BestExecutableSellPrice.GetValueOrDefault();
        var hasExecutableAsk = HasExecutableAsk(quote);
        var buyLimit = hasExecutableAsk
            ? bestAsk
            : PickAutomatedPassiveLimitPrice(context.Participant.Temperament, quote);
        if (!hasExecutableAsk && buyLimit <= quote.Price)
        {
            return null;
        }

        var envelope = automatedBuyOrderPolicy.BuildBuyEnvelope(new AutomatedBuyOrderInput(
            context.Participant.RiskProfile,
            context.NetWorth,
            context.HoldingsValue,
            context.ReservedBuyNotional,
            context.AvailableBalance,
            context.BuyingPower,
            context.MarginLiability,
            buyLimit,
            quote.IssuedShares,
            hasExecutableAsk ? quote.BestExecutableSellQuantity : 0));
        if (envelope is null)
        {
            return null;
        }

        var sizedQuantity = tradeSizer.Size(context.Participant.Temperament, envelope.MaximumQuantity);
        var quantity = Math.Clamp(sizedQuantity, envelope.MinimumQuantity, envelope.MaximumQuantity);
        return new OrderIntent(OrderType.Buy, quote.CompanyId, quantity, buyLimit);
    }

    private decimal PickAutomatedPassiveLimitPrice(Temperament temperament, CompanyQuote quote)
    {
        var options = automatedTradingOptions.Value;
        var totalRange = options.PassiveBuyPremiumMaxPercent - options.PassiveBuyPremiumMinPercent;
        var third = totalRange / 3m;
        var (minimum, maximum) = temperament switch
        {
            Temperament.Conservative => (
                options.PassiveBuyPremiumMinPercent,
                options.PassiveBuyPremiumMinPercent + third),
            Temperament.Aggressive => (
                options.PassiveBuyPremiumMaxPercent - third,
                options.PassiveBuyPremiumMaxPercent),
            _ => (
                options.PassiveBuyPremiumMinPercent + third,
                options.PassiveBuyPremiumMaxPercent - third),
        };
        var premiumPercent = minimum + ((decimal)random.NextDouble() * (maximum - minimum));
        var proposed = Round(quote.Price * (1m + (premiumPercent / 100m)));
        return Math.Min(proposed, quote.Bounds!.ActiveUpperPrice);
    }

    private AutomatedExposureAssessment? AutomatedExposure(DecisionContext context) =>
        UsesAutomatedBuyPolicy(context)
            ? automatedBuyOrderPolicy.AssessExposure(
                context.Participant.RiskProfile,
                context.NetWorth,
                context.HoldingsValue)
            : null;

    private static bool UsesAutomatedBuyPolicy(DecisionContext context) =>
        context.Participant.Type == ParticipantType.Individual && context.HasAutomatedTradingData;

    private static bool IsAutomatedPassiveBuy(DecisionContext context, CompanyQuote quote) =>
        UsesAutomatedBuyPolicy(context)
        && !HasExecutableAsk(quote)
        && quote.OpenSellQuantity == 0
        && IsRisingOrStable(quote);

    // A company counts as rising or stable when its price is not below the level from roughly ten cycles ago,
    // so automated above-market bids lift healthy names instead of propping up decliners.
    private static bool IsRisingOrStable(CompanyQuote quote) => quote.LongRangeChangePct >= 0m;

    private static bool HasExecutableAsk(CompanyQuote quote) =>
        quote.BestExecutableSellPrice is decimal bestAsk
        && quote.BestExecutableSellQuantity > 0
        && quote.Bounds!.IsWithinActiveBand(bestAsk);

    // Legacy pricing draws, in order: one draw decides inside-band versus a waiting outer segment
    // (OutsideBandOrder chance), then one draw places the price. Automated Individual buys bypass this path so
    // funds and old callers retain their exact random sequence.
    private decimal PickLimitPrice(CompanyQuote quote, OrderType type)
    {
        var bounds = quote.Bounds!;
        if (random.NextDouble() < chanceRates.Value.EventTriggerChances.OutsideBandOrder)
        {
            return OuterSegmentPrice(bounds);
        }

        return PickInsideLimitPrice(quote, type);
    }

    private decimal PickInsideLimitPrice(CompanyQuote quote, OrderType type)
    {
        var bounds = quote.Bounds!;
        var pricingReference = Math.Max(quote.Price, bounds.ReferencePrice);
        var inside = type == OrderType.Buy
            ? Round(pricingReference * (1m + RandomOffset()))
            : Round(pricingReference * (1m + SymmetricOffset()));
        return Math.Clamp(inside, bounds.ActiveLowerPrice, bounds.ActiveUpperPrice);
    }

    // A single draw selects a cent-aligned position across the union of the lower waiting segment
    // [allowedMin, activeLower) and the upper one (activeUpper, allowedMax], so every result rests strictly
    // outside the active band yet inside the allowed range.
    private decimal OuterSegmentPrice(OrderPriceBounds bounds)
    {
        var lowerCents = (int)Math.Round((bounds.ActiveLowerPrice - bounds.AllowedMinimumPrice) / 0.01m);
        var upperCents = (int)Math.Round((bounds.AllowedMaximumPrice - bounds.ActiveUpperPrice) / 0.01m);
        var totalCents = lowerCents + upperCents;
        if (totalCents <= 0)
        {
            return bounds.ActiveLowerPrice;
        }

        var index = Math.Min(totalCents - 1, (int)((decimal)random.NextDouble() * totalCents));
        var price = index < lowerCents
            ? bounds.AllowedMinimumPrice + (index * 0.01m)
            : bounds.ActiveUpperPrice + ((index - lowerCents + 1) * 0.01m);
        return Round(price);
    }

    private decimal RandomOffset() =>
        MinPriceOffset + ((decimal)random.NextDouble() * (MaxPriceOffset - MinPriceOffset));

    // Rule-based sells no longer systematically undercut: one draw spreads the ask symmetrically around the
    // reference, so its expected price is the reference and the book stops drifting down on its own.
    private decimal SymmetricOffset() =>
        (((decimal)random.NextDouble() * 2m) - 1m) * MaxPriceOffset;

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
