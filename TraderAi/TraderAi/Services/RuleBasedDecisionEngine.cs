using System.Collections.ObjectModel;
using Microsoft.Extensions.Options;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record TradeActionProbabilities(
    decimal Buy,
    decimal Sell,
    decimal Wait);

public sealed record RuleBasedDecisionEvaluation(
    IReadOnlyDictionary<int, decimal> DirectionalScores,
    int? BuyTargetCompanyId,
    int? SellTargetCompanyId,
    TradeActionProbabilities Probabilities);

// Directional evidence is evaluated independently from portfolio and execution pressure. The final action
// distribution is sampled once; any later draws size or price the already selected action.
public sealed class RuleBasedDecisionEngine(
    ITradeSizer tradeSizer,
    IOptions<RandomChanceRatesOptions> chanceRates,
    Random random,
    AutomatedBuyOrderPolicy? automatedBuyOrderPolicy = null,
    IOptions<AutomatedTradingOptions>? automatedTradingOptions = null,
    IOptions<TradingSignalOptions>? tradingSignalOptions = null) : IDecisionEngine
{
    private readonly IOptions<AutomatedTradingOptions> automatedTradingOptions = automatedTradingOptions
        ?? Options.Create(new AutomatedTradingOptions());
    private readonly AutomatedBuyOrderPolicy automatedBuyOrderPolicy = automatedBuyOrderPolicy
        ?? new AutomatedBuyOrderPolicy(automatedTradingOptions ?? Options.Create(new AutomatedTradingOptions()));
    private readonly TradingSignalOptions tradingSignals =
        tradingSignalOptions?.Value ?? new TradingSignalOptions();

    private const decimal ExtremeMoveThreshold = 0.60m;
    private const decimal MaximumDebtRatio = 0.40m;

    public RuleBasedDecisionEvaluation Evaluate(DecisionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var directionalScores = context.Companies.ToDictionary(
            quote => quote.CompanyId,
            quote => DirectionalScore(quote, context.Participant.RiskProfile));
        var readonlyScores = new ReadOnlyDictionary<int, decimal>(directionalScores);

        var sellCandidates = context.Companies
            .Where(quote => quote.Bounds is not null
                && !context.CompaniesWithOpenOrders.Contains(quote.CompanyId)
                && context.SharesOwnedByCompany.GetValueOrDefault(quote.CompanyId) > 0)
            .ToList();

        var exposure = AutomatedExposure(context);
        var buyBlockedByExposure = exposure is not null
            && exposure.CurrentExposurePercent >= exposure.Target.MaximumExposurePercent;
        var usesAutomatedBuyPolicy = UsesAutomatedBuyPolicy(context);
        var buyCandidates = context.AvailableCash > 0m && !buyBlockedByExposure
            ? context.Companies
                .Where(quote => IsEligibleBuyCandidate(
                    context,
                    quote,
                    usesAutomatedBuyPolicy))
                .ToList()
            : [];

        var actionableBuyCandidates = buyCandidates;
        if (exposure?.Position == AutomatedExposurePosition.Below)
        {
            var executableCandidates = buyCandidates.Where(HasExecutableAsk).ToList();
            if (executableCandidates.Count > 0)
            {
                actionableBuyCandidates = executableCandidates;
            }
        }

        var buyTarget = actionableBuyCandidates
            .OrderByDescending(quote => directionalScores[quote.CompanyId])
            .ThenBy(quote => quote.CompanyId)
            .FirstOrDefault();
        var sellTarget = sellCandidates
            .OrderBy(quote => directionalScores[quote.CompanyId])
            .ThenBy(quote => quote.CompanyId)
            .FirstOrDefault();

        var probabilities = BuildProbabilities(
            context,
            exposure,
            buyTarget,
            sellTarget,
            directionalScores);

        return new RuleBasedDecisionEvaluation(
            readonlyScores,
            buyTarget?.CompanyId,
            sellTarget?.CompanyId,
            probabilities);
    }

    public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
    {
        var evaluation = Evaluate(context);
        var roll = (decimal)random.NextDouble();

        if (roll < evaluation.Probabilities.Buy)
        {
            var target = FindCompany(context, evaluation.BuyTargetCompanyId)
                ?? throw new InvalidOperationException("A selected buy must have an eligible target.");
            var intent = BuildBuy(context, target)
                ?? throw new InvalidOperationException("A selected buy could not produce a valid order.");
            var gated = GateBuy(intent, context);
            return gated.Count == 0
                ? gated
                : FanOutBuys(context, intent, evaluation);
        }

        if (roll < evaluation.Probabilities.Buy + evaluation.Probabilities.Sell)
        {
            var target = FindCompany(context, evaluation.SellTargetCompanyId)
                ?? throw new InvalidOperationException("A selected sell must have an eligible target.");
            var intent = BuildSell(context, target)
                ?? throw new InvalidOperationException("A selected sell could not produce a valid order.");
            return [intent];
        }

        return [];
    }

    private TradeActionProbabilities BuildProbabilities(
        DecisionContext context,
        AutomatedExposureAssessment? exposure,
        CompanyQuote? buyTarget,
        CompanyQuote? sellTarget,
        IReadOnlyDictionary<int, decimal> directionalScores)
    {
        if (buyTarget is null && sellTarget is null)
        {
            return new TradeActionProbabilities(0m, 0m, 1m);
        }

        var buyEvidence = buyTarget is null
            ? 0m
            : Math.Max(0m, directionalScores[buyTarget.CompanyId]);
        var sellEvidence = sellTarget is null
            ? 0m
            : Math.Max(0m, -directionalScores[sellTarget.CompanyId]);
        var strongestEvidence = Math.Max(buyEvidence, sellEvidence);
        var activity = ActivityFactor(context.Participant.Temperament);

        var buyWeight = tradingSignals.EvidenceWeight * buyEvidence
            + tradingSignals.PersonalityNoiseWeight * activity;
        var sellWeight = tradingSignals.EvidenceWeight * sellEvidence
            + tradingSignals.PersonalityNoiseWeight * activity;
        var waitWeight = tradingSignals.EvidenceWeight
                * (tradingSignals.MinimumWaitWeight + (1m - strongestEvidence))
            + tradingSignals.PersonalityNoiseWeight / activity;

        if (buyTarget is not null)
        {
            buyWeight += BargainPressure(context, buyTarget);
            if (exposure?.Position == AutomatedExposurePosition.Below)
            {
                buyWeight += tradingSignals.MinimumWaitWeight;
            }

            if (IsAutomatedPassiveBuy(context, buyTarget))
            {
                buyWeight *= (decimal)chanceRates.Value.EventTriggerChances.NoSellOrderBuyChance;
            }
        }

        if (sellTarget is not null)
        {
            sellWeight += ProfitTakingPressure(sellTarget);
            sellWeight += DebtSellPressure(context);
        }

        if (buyTarget is null)
        {
            buyWeight = 0m;
        }

        if (sellTarget is null)
        {
            sellWeight = 0m;
        }

        return Normalize(buyWeight, sellWeight, waitWeight);
    }

    private decimal DirectionalScore(CompanyQuote quote, RiskProfile riskProfile)
    {
        var momentum = NormalizeMomentum(quote);
        var orderFlow = Math.Clamp(quote.OrderFlowImbalance, -1m, 1m);
        var industry = Math.Clamp(quote.SectorSentiment / 1_000m, -1m, 1m);
        var audit = AuditDirection(quote.Audit?.Rating);
        var fundamentals = FundamentalDirection(quote.Financials, riskProfile);

        return Math.Clamp(
            momentum * tradingSignals.MomentumWeight
            + orderFlow * tradingSignals.OrderFlowWeight
            + industry * tradingSignals.IndustryWeight
            + audit * tradingSignals.AuditWeight
            + fundamentals * tradingSignals.FundamentalWeight,
            -1m,
            1m);
    }

    private static decimal NormalizeMomentum(CompanyQuote quote)
    {
        if (quote.PriceChangePct == 0m)
        {
            return 0m;
        }

        var fullScale = quote.Bounds is null || quote.Bounds.ReferencePrice <= 0m
            ? 1m
            : Math.Max(
                (quote.Bounds.ActiveUpperPrice - quote.Bounds.ReferencePrice)
                    / quote.Bounds.ReferencePrice,
                (quote.Bounds.ReferencePrice - quote.Bounds.ActiveLowerPrice)
                    / quote.Bounds.ReferencePrice);
        return fullScale <= 0m
            ? 0m
            : Math.Clamp(quote.PriceChangePct / fullScale, -1m, 1m);
    }

    private static decimal AuditDirection(CompanyRiskRating? rating) => rating switch
    {
        CompanyRiskRating.ExtraRaisedExpectations => 1m,
        CompanyRiskRating.RaisedExpectations => 0.5m,
        CompanyRiskRating.LowRisk => -0.5m,
        CompanyRiskRating.HighRisk => -1m,
        _ => 0m,
    };

    private decimal FundamentalDirection(
        LatestFinancialEvidence? financials,
        RiskProfile riskProfile)
    {
        if (financials is null)
        {
            return 0m;
        }

        var profitability = CenterScore(financials.ProfitabilityScore);
        var stability = CenterScore(financials.StabilityScore);
        var closureSafety = -CenterScore(financials.ClosureRiskScore);
        var dividendCoverage = Math.Clamp(
            financials.Current.DividendCoverageRatio - 1m,
            -1m,
            1m);
        var quality = (profitability + stability + closureSafety + dividendCoverage) / 4m;

        var guidance = financials.ManagementOutlook switch
        {
            ManagementOutlook.Positive => UnitScore(financials.ManagementConfidenceScore),
            ManagementOutlook.Negative => -UnitScore(financials.ManagementConfidenceScore),
            _ => 0m,
        };
        var forecastGrowth = (
            RelativeChange(
                financials.Current.Revenue,
                financials.Current.ManagementRevenueForecast)
            + RelativeChange(
                financials.Current.NetProfit,
                financials.Current.ManagementProfitForecast)
            + RelativeChange(
                financials.Current.OperatingCashFlow,
                financials.Current.ManagementOperatingCashFlowForecast)) / 3m;
        var growth = (guidance + forecastGrowth) / 2m;

        var qualityFactor = QualityResponseFactor(riskProfile);
        var growthFactor = GrowthResponseFactor(riskProfile);
        return Math.Clamp(
            ((quality * qualityFactor) + (growth * growthFactor))
                / (qualityFactor + growthFactor),
            -1m,
            1m);
    }

    private decimal ActivityFactor(Temperament temperament) => temperament switch
    {
        Temperament.Aggressive => tradingSignals.AggressiveActivityFactor,
        Temperament.Conservative => tradingSignals.ConservativeActivityFactor,
        _ => tradingSignals.BalancedActivityFactor,
    };

    private decimal QualityResponseFactor(RiskProfile riskProfile) => riskProfile switch
    {
        RiskProfile.Low => tradingSignals.LowRiskQualityResponseFactor,
        RiskProfile.High => tradingSignals.HighRiskQualityResponseFactor,
        _ => tradingSignals.MediumRiskQualityResponseFactor,
    };

    private decimal GrowthResponseFactor(RiskProfile riskProfile) => riskProfile switch
    {
        RiskProfile.Low => tradingSignals.LowRiskGrowthResponseFactor,
        RiskProfile.High => tradingSignals.HighRiskGrowthResponseFactor,
        _ => tradingSignals.MediumRiskGrowthResponseFactor,
    };

    private static decimal CenterScore(decimal score) =>
        Math.Clamp((score - 50m) / 50m, -1m, 1m);

    private static decimal UnitScore(decimal score) =>
        Math.Clamp(score / 100m, 0m, 1m);

    private static decimal RelativeChange(decimal current, decimal forecast)
    {
        var denominator = Math.Max(Math.Abs(current), 1m);
        return Math.Clamp((forecast - current) / denominator, -1m, 1m);
    }

    private decimal BargainPressure(DecisionContext context, CompanyQuote quote)
    {
        if (context.SharesOwnedByCompany.GetValueOrDefault(quote.CompanyId) > 0
            || quote.LongRangeChangePct >= 0m)
        {
            return 0m;
        }

        var magnitude = Math.Clamp(-quote.LongRangeChangePct / ExtremeMoveThreshold, 0m, 1m);
        return magnitude * tradingSignals.PersonalityNoiseWeight;
    }

    private decimal ProfitTakingPressure(CompanyQuote quote)
    {
        if (quote.LongRangeChangePct <= 0m)
        {
            return 0m;
        }

        var magnitude = Math.Clamp(quote.LongRangeChangePct / ExtremeMoveThreshold, 0m, 1m);
        return magnitude * tradingSignals.PersonalityNoiseWeight;
    }

    private decimal DebtSellPressure(DecisionContext context)
    {
        if (context.LoanLiability <= 0m)
        {
            return 0m;
        }

        var holdingsValue = context.HoldingsValue > 0m
            ? context.HoldingsValue
            : context.Companies.Sum(quote =>
                context.SharesOwnedByCompany.GetValueOrDefault(quote.CompanyId)
                * quote.Price);
        var grossWorth = Math.Max(
            context.Participant.CurrentBalance + holdingsValue,
            context.LoanLiability);
        var debtRatio = grossWorth > 0m
            ? Math.Clamp(context.LoanLiability / grossWorth, 0m, MaximumDebtRatio)
            : MaximumDebtRatio;
        return debtRatio * QualityResponseFactor(context.Participant.RiskProfile);
    }

    private static TradeActionProbabilities Normalize(
        decimal buyWeight,
        decimal sellWeight,
        decimal waitWeight)
    {
        buyWeight = Math.Max(0m, buyWeight);
        sellWeight = Math.Max(0m, sellWeight);
        waitWeight = Math.Max(0m, waitWeight);
        var total = buyWeight + sellWeight + waitWeight;
        if (total <= 0m)
        {
            return new TradeActionProbabilities(0m, 0m, 1m);
        }

        var buy = buyWeight / total;
        var sell = sellWeight / total;
        return new TradeActionProbabilities(buy, sell, 1m - buy - sell);
    }

    private bool IsEligibleBuyCandidate(
        DecisionContext context,
        CompanyQuote quote,
        bool usesAutomatedBuyPolicy)
    {
        if (quote.Bounds is null
            || context.CompaniesWithOpenOrders.Contains(quote.CompanyId)
            || (usesAutomatedBuyPolicy && quote.IndividualBuyBlockedForBatch))
        {
            return false;
        }

        if (!usesAutomatedBuyPolicy)
        {
            return context.AvailableCash >= quote.Bounds.ActiveUpperPrice;
        }

        if (HasExecutableAsk(quote))
        {
            return CanBuildAutomatedBuy(context, quote, quote.BestExecutableSellPrice!.Value);
        }

        return quote.OpenSellQuantity == 0
            && IsRisingOrStable(quote)
            && quote.Bounds.ActiveUpperPrice > quote.Price
            && chanceRates.Value.EventTriggerChances.NoSellOrderBuyChance > 0d
            && CanBuildAutomatedBuy(context, quote, quote.Bounds.ActiveUpperPrice);
    }

    private bool CanBuildAutomatedBuy(
        DecisionContext context,
        CompanyQuote quote,
        decimal price)
    {
        var envelope = automatedBuyOrderPolicy.BuildBuyEnvelope(new AutomatedBuyOrderInput(
            context.Participant.RiskProfile,
            context.NetWorth,
            context.HoldingsValue,
            context.ReservedBuyNotional,
            context.AvailableBalance,
            context.BuyingPower,
            context.MarginLiability,
            price,
            quote.IssuedShares,
            HasExecutableAsk(quote) ? quote.BestExecutableSellQuantity : 0));
        return envelope is not null;
    }

    private OrderIntent? BuildSell(DecisionContext context, CompanyQuote quote)
    {
        var sharesOwned = context.SharesOwnedByCompany.GetValueOrDefault(quote.CompanyId);
        var quantity = tradeSizer.Size(context.Participant.Temperament, sharesOwned);
        if (quantity < 1)
        {
            return null;
        }

        var sellLimit = PickPassiveLimitPrice(quote);
        return new OrderIntent(
            OrderType.Sell,
            quote.CompanyId,
            quantity,
            sellLimit);
    }

    private OrderIntent? BuildBuy(DecisionContext context, CompanyQuote quote)
    {
        if (UsesAutomatedBuyPolicy(context))
        {
            return BuildAutomatedIndividualBuy(context, quote);
        }

        var buyLimit = PickPassiveLimitPrice(quote);
        var maxAffordable = (int)Math.Clamp(
            Math.Floor(context.AvailableCash / buyLimit),
            0m,
            int.MaxValue);
        var quantity = tradeSizer.Size(context.Participant.Temperament, maxAffordable);
        return quantity >= 1
            ? new OrderIntent(OrderType.Buy, quote.CompanyId, quantity, buyLimit)
            : null;
    }

    private OrderIntent? BuildAutomatedIndividualBuy(
        DecisionContext context,
        CompanyQuote quote)
    {
        var hasExecutableAsk = HasExecutableAsk(quote);
        var buyLimit = hasExecutableAsk
            ? quote.BestExecutableSellPrice!.Value
            : PickAutomatedPassiveLimitPrice(
                context.Participant.Temperament,
                quote);
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

        var sizedQuantity = tradeSizer.Size(
            context.Participant.Temperament,
            envelope.MaximumQuantity);
        var quantity = Math.Clamp(
            sizedQuantity,
            envelope.MinimumQuantity,
            envelope.MaximumQuantity);
        return new OrderIntent(
            OrderType.Buy,
            quote.CompanyId,
            quantity,
            buyLimit);
    }

    private IReadOnlyList<OrderIntent> GateBuy(
        OrderIntent intent,
        DecisionContext context)
    {
        var keep = CrisisBuyKeepProbability(
            context.Participant,
            context.CrisisActive);
        if (keep < 1m && (decimal)random.NextDouble() >= keep)
        {
            return [];
        }

        return [intent];
    }

    private decimal CrisisBuyKeepProbability(
        Participant participant,
        bool crisisActive)
    {
        if (!crisisActive)
        {
            return 1m;
        }

        var suppression = (decimal)chanceRates.Value.ChanceModifiers.CrisisBuySuppression;
        var keep = 1m;
        if (participant.Temperament == Temperament.Conservative)
        {
            keep *= 1m - suppression;
        }

        if (participant.RiskProfile == RiskProfile.Low)
        {
            keep *= 1m - suppression;
        }

        return Math.Clamp(keep, 0m, 1m);
    }

    private IReadOnlyList<OrderIntent> FanOutBuys(
        DecisionContext context,
        OrderIntent firstBuy,
        RuleBasedDecisionEvaluation evaluation)
    {
        var options = automatedTradingOptions.Value;
        var minimum = Math.Max(1, options.BuyOrdersPerCycleMin);
        var maximum = Math.Max(minimum, options.BuyOrdersPerCycleMax);
        var count = minimum == maximum
            ? minimum
            : random.Next(minimum, maximum + 1);
        if (count == 1)
        {
            return [firstBuy];
        }

        var buys = new List<OrderIntent>(count) { firstBuy };
        var used = new HashSet<int> { firstBuy.CompanyId };
        var usesAutomatedBuyPolicy = UsesAutomatedBuyPolicy(context);
        var candidates = context.Companies
            .Where(quote => IsEligibleBuyCandidate(
                context,
                quote,
                usesAutomatedBuyPolicy))
            .ToList();
        if (AutomatedExposure(context)?.Position == AutomatedExposurePosition.Below
            && candidates.Any(HasExecutableAsk))
        {
            candidates = candidates.Where(HasExecutableAsk).ToList();
        }

        foreach (var quote in candidates
            .OrderByDescending(quote =>
                evaluation.DirectionalScores.GetValueOrDefault(quote.CompanyId))
            .ThenBy(quote => quote.CompanyId))
        {
            if (buys.Count >= count)
            {
                break;
            }

            if (!used.Add(quote.CompanyId))
            {
                continue;
            }

            if (BuildBuy(context, quote) is { } extraBuy)
            {
                buys.Add(extraBuy);
            }
        }

        return buys;
    }

    private decimal PickPassiveLimitPrice(CompanyQuote quote)
    {
        var bounds = quote.Bounds!;
        var reference = Math.Clamp(
            Math.Max(quote.Price, bounds.ReferencePrice),
            bounds.ActiveLowerPrice,
            bounds.ActiveUpperPrice);
        var draw = (decimal)random.NextDouble();
        var centered = (draw * 2m) - 1m;
        var magnitudes = chanceRates.Value.RandomMagnitudeBands;
        var minimum = magnitudes.PassivePriceOffsetMinPercent / 100m;
        var maximum = magnitudes.PassivePriceOffsetMaxPercent / 100m;
        var offset = centered == 0m
            ? 0m
            : Math.Sign(centered)
                * (minimum + ((maximum - minimum) * Math.Abs(centered)));
        var proposed = Round(reference * (1m + offset));
        return Math.Clamp(
            proposed,
            bounds.ActiveLowerPrice,
            bounds.ActiveUpperPrice);
    }

    private decimal PickAutomatedPassiveLimitPrice(
        Temperament temperament,
        CompanyQuote quote)
    {
        var options = automatedTradingOptions.Value;
        var totalRange = options.PassiveBuyPremiumMaxPercent
            - options.PassiveBuyPremiumMinPercent;
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
        var premiumPercent = minimum
            + ((decimal)random.NextDouble() * (maximum - minimum));
        var proposed = Round(
            quote.Price * (1m + (premiumPercent / 100m)));
        return Math.Min(proposed, quote.Bounds!.ActiveUpperPrice);
    }

    private AutomatedExposureAssessment? AutomatedExposure(
        DecisionContext context) =>
        UsesAutomatedBuyPolicy(context)
            ? automatedBuyOrderPolicy.AssessExposure(
                context.Participant.RiskProfile,
                context.NetWorth,
                context.HoldingsValue)
            : null;

    private static CompanyQuote? FindCompany(
        DecisionContext context,
        int? companyId) =>
        companyId is null
            ? null
            : context.Companies.FirstOrDefault(
                quote => quote.CompanyId == companyId);

    private static bool UsesAutomatedBuyPolicy(DecisionContext context) =>
        context.Participant.Type == ParticipantType.Individual
        && context.HasAutomatedTradingData;

    private static bool IsAutomatedPassiveBuy(
        DecisionContext context,
        CompanyQuote quote) =>
        UsesAutomatedBuyPolicy(context)
        && !HasExecutableAsk(quote)
        && quote.OpenSellQuantity == 0
        && IsRisingOrStable(quote);

    private static bool IsRisingOrStable(CompanyQuote quote) =>
        quote.LongRangeChangePct >= 0m;

    private static bool HasExecutableAsk(CompanyQuote quote) =>
        quote.BestExecutableSellPrice is decimal bestAsk
        && quote.BestExecutableSellQuantity > 0
        && quote.Bounds!.IsWithinActiveBand(bestAsk);

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
