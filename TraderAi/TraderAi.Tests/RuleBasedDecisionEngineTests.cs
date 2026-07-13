using Microsoft.Extensions.Options;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class RuleBasedDecisionEngineTests
{
    // MaxTradeSizer returns the full cap, so quantity assertions probe the upper bound the engine sets.
    private readonly RuleBasedDecisionEngine engine = new(new MaxTradeSizer(), Options.Create(new RandomChanceRatesOptions()), new Random(20260619));

    [Fact]
    public void OnlySellsSharesItOwnsAndNeverMoreThanOwned()
    {
        var context = ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m);

        var intents = CollectIntents(context);

        Assert.NotEmpty(intents);
        Assert.All(intents, intent =>
        {
            Assert.Equal(OrderType.Sell, intent.Type);
            Assert.Equal(1, intent.CompanyId);
            Assert.InRange(intent.Quantity, 1, 10);
        });
    }

    [Fact]
    public void BuyNeverReservesMoreThanAvailableCash()
    {
        var context = ContextFor(availableCash: 5000m, sharesOwned: 0, companyPrice: 100m);

        var intents = CollectIntents(context);

        Assert.NotEmpty(intents);
        Assert.All(intents, intent =>
        {
            Assert.Equal(OrderType.Buy, intent.Type);
            Assert.True(intent.Quantity * intent.LimitPrice <= 5000m);
        });
    }

    [Fact]
    public void ActiveCrisisPullsConservativeLowRiskTradersBackFromBuying()
    {
        var calm = CountBuysUnderCrisis(Temperament.Conservative, RiskProfile.Low, crisisActive: false);
        var stressed = CountBuysUnderCrisis(Temperament.Conservative, RiskProfile.Low, crisisActive: true);

        Assert.True(
            stressed < calm,
            $"a crisis should suppress a conservative low-risk trader's buys (calm {calm}, stressed {stressed})");
    }

    [Fact]
    public void ActiveCrisisLeavesBalancedMediumTradersBuyingUnchanged()
    {
        // No matching trait means no suppression and no extra draw, so the outcome is bit-for-bit identical.
        var calm = CountBuysUnderCrisis(Temperament.Balanced, RiskProfile.Medium, crisisActive: false);
        var stressed = CountBuysUnderCrisis(Temperament.Balanced, RiskProfile.Medium, crisisActive: true);

        Assert.Equal(calm, stressed);
    }

    // A strong-buy, no-shares market so every action is a buy or a skip; a fresh, equally seeded engine keeps
    // the only difference the crisis flag and personality.
    private static int CountBuysUnderCrisis(Temperament temperament, RiskProfile riskProfile, bool crisisActive)
    {
        var engine = new RuleBasedDecisionEngine(new MaxTradeSizer(), Options.Create(new RandomChanceRatesOptions()), new Random(20260705));
        var context = new DecisionContext(
            NewParticipant(availableCash: 50_000m, temperament, riskProfile),
            AvailableCash: 50_000m,
            [new CompanyQuote(1, Price: 100m, PriceChangePct: 0.10m, NetShareDemand: 1000, Bounds: Bounds(100m))],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            crisisActive);

        var buys = 0;
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            buys += engine.Decide(context).Count(intent => intent.Type == OrderType.Buy);
        }

        return buys;
    }

    [Fact]
    public void NeverActsOnACompanyThatAlreadyHasAnOpenOrder()
    {
        var context = ContextFor(
            availableCash: 5000m,
            sharesOwned: 10,
            companyPrice: 100m,
            companiesWithOpenOrders: [1]);

        Assert.Empty(CollectIntents(context));
    }

    [Fact]
    public void DoesNothingWhenItHasNoCashAndNoShares()
    {
        var context = ContextFor(availableCash: 0m, sharesOwned: 0, companyPrice: 100m);

        Assert.Empty(CollectIntents(context));
    }

    // Draw order per built order: decision roll, then the inside/outside roll, then the price-position draw. A
    // roll that does not clear the OutsideBandOrder chance keeps the buy inside the band at the 1-5% market offset.
    [Fact]
    public void InsideBandBuySitsOneToFivePercentAboveMarketWhenTheOutsideRollDoesNotPass()
    {
        var buy = EngineWith([0.05d, 0.50d, 0d])
            .Decide(ContextFor(availableCash: 5000m, sharesOwned: 0, companyPrice: 100m, priceChangePct: 0.10m));

        Assert.Collection(buy, intent =>
        {
            Assert.Equal(OrderType.Buy, intent.Type);
            Assert.Equal(101m, intent.LimitPrice);
        });
    }

    [Fact]
    public void InsideBandSellSitsOneToFivePercentBelowMarketWhenTheOutsideRollDoesNotPass()
    {
        var sell = EngineWith([0.05d, 0.50d, 0d])
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, priceChangePct: -0.10m));

        Assert.Collection(sell, intent =>
        {
            Assert.Equal(OrderType.Sell, intent.Type);
            Assert.Equal(99m, intent.LimitPrice);
        });
    }

    [Fact]
    public void OutsideRollPlacesABuyInTheLowerWaitingSegmentBelowTheBand()
    {
        var bounds = Bounds(100m);
        var buy = EngineWith([0.05d, 0.05d, 0d])
            .Decide(ContextFor(availableCash: 5000m, sharesOwned: 0, companyPrice: 100m, priceChangePct: 0.10m));

        Assert.Collection(buy, intent =>
        {
            Assert.Equal(OrderType.Buy, intent.Type);
            Assert.Equal(75m, intent.LimitPrice);
            Assert.False(bounds.IsWithinActiveBand(intent.LimitPrice));
            Assert.True(bounds.IsWithinAllowedRange(intent.LimitPrice));
        });
    }

    [Fact]
    public void OutsideRollPlacesASellInTheUpperWaitingSegmentAboveTheBand()
    {
        var bounds = Bounds(100m);
        var sell = EngineWith([0.05d, 0.05d, 0.90d])
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, priceChangePct: -0.10m));

        Assert.Collection(sell, intent =>
        {
            Assert.Equal(OrderType.Sell, intent.Type);
            Assert.True(intent.LimitPrice > bounds.ActiveUpperPrice);
            Assert.True(bounds.IsWithinAllowedRange(intent.LimitPrice));
        });
    }

    [Fact]
    public void BothSidesCanRestInEitherWaitingSegment()
    {
        var bounds = Bounds(100m);

        var buyLower = EngineWith([0.05d, 0.05d, 0d])
            .Decide(ContextFor(availableCash: 5000m, sharesOwned: 0, companyPrice: 100m, priceChangePct: 0.10m));
        var buyUpper = EngineWith([0.05d, 0.05d, 0.90d])
            .Decide(ContextFor(availableCash: 5000m, sharesOwned: 0, companyPrice: 100m, priceChangePct: 0.10m));
        var sellLower = EngineWith([0.05d, 0.05d, 0d])
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, priceChangePct: -0.10m));
        var sellUpper = EngineWith([0.05d, 0.05d, 0.90d])
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, priceChangePct: -0.10m));

        Assert.Equal(OrderType.Buy, buyLower.Single().Type);
        Assert.True(buyLower.Single().LimitPrice < bounds.ActiveLowerPrice);
        Assert.True(buyUpper.Single().LimitPrice > bounds.ActiveUpperPrice);
        Assert.True(sellLower.Single().LimitPrice < bounds.ActiveLowerPrice);
        Assert.True(sellUpper.Single().LimitPrice > bounds.ActiveUpperPrice);
        Assert.All(
            new[] { buyLower, buyUpper, sellLower, sellUpper },
            intents => Assert.True(bounds.IsWithinAllowedRange(intents.Single().LimitPrice)));
    }

    [Fact]
    public void NoGeneratedPriceEverLeavesTheAllowedRange()
    {
        var bounds = Bounds(100m);
        var context = ContextFor(availableCash: 5000m, sharesOwned: 10, companyPrice: 100m);

        Assert.All(CollectIntents(context), intent =>
            Assert.True(bounds.IsWithinAllowedRange(intent.LimitPrice), $"price {intent.LimitPrice} left the allowed range"));
    }

    // One built order draws exactly three doubles — decision, inside/outside, position — and no integer draws.
    [Fact]
    public void APricedOrderExhaustsExactlyTheScriptedDrawOrder()
    {
        var random = new ScriptedRandom([0.05d, 0.50d, 0d], []);
        var intents = new RuleBasedDecisionEngine(new MaxTradeSizer(), Options.Create(new RandomChanceRatesOptions()), random)
            .Decide(ContextFor(availableCash: 5000m, sharesOwned: 0, companyPrice: 100m, priceChangePct: 0.10m));

        Assert.Single(intents);
        Assert.Equal(3, random.DoubleDrawCount);
        Assert.Equal(0, random.IntegerDrawCount);
    }

    private static RuleBasedDecisionEngine EngineWith(double[] doubles) =>
        new(new MaxTradeSizer(), Options.Create(new RandomChanceRatesOptions()), new ScriptedRandom(doubles, []));

    [Fact]
    public void WithNoSignalBothBuyingAndSellingStayReachable()
    {
        var context = ContextFor(availableCash: 5000m, sharesOwned: 10, companyPrice: 100m);

        var intents = CollectIntents(context);

        Assert.Contains(intents, intent => intent.Type == OrderType.Buy);
        Assert.Contains(intents, intent => intent.Type == OrderType.Sell);
    }

    [Fact]
    public void PrefersBuyingTheCompanyWithTheStrongerSignal()
    {
        var participant = NewParticipant(availableCash: 50_000m);
        var context = new DecisionContext(
            participant,
            AvailableCash: 50_000m,
            [
                new CompanyQuote(1, Price: 100m, PriceChangePct: 0.10m, NetShareDemand: 1000, Bounds: Bounds(100m)),
                new CompanyQuote(2, Price: 100m, Bounds: Bounds(100m)),
            ],
            new Dictionary<int, int>(),
            new HashSet<int>());

        var buys = CollectIntents(context).Where(intent => intent.Type == OrderType.Buy).ToList();

        Assert.NotEmpty(buys);
        Assert.True(
            buys.Count(intent => intent.CompanyId == 1) > buys.Count(intent => intent.CompanyId == 2),
            "the company with rising price and net buy demand should be bought more often");
    }

    [Fact]
    public void FallingPriceMakesOwnersSellMoreThanTheyBuy()
    {
        var context = ContextFor(availableCash: 5000m, sharesOwned: 10, companyPrice: 100m, priceChangePct: -0.10m);

        var intents = CollectIntents(context);

        var sells = intents.Count(intent => intent.Type == OrderType.Sell);
        var buys = intents.Count(intent => intent.Type == OrderType.Buy);
        Assert.True(sells > buys, "a recent decline should pull owners toward selling over buying");
    }

    [Fact]
    public void ExtremeRiseMakesOwnersTakeProfit()
    {
        var context = ContextFor(availableCash: 5000m, sharesOwned: 10, companyPrice: 100m, longRangeChangePct: 0.80m);

        var intents = CollectIntents(context);

        var sells = intents.Count(intent => intent.Type == OrderType.Sell);
        var buys = intents.Count(intent => intent.Type == OrderType.Buy);
        Assert.True(sells > buys, "a large run-up should pull owners toward taking profit");
    }

    [Fact]
    public void ExtremeDropMakesNonOwnersBuyTheDipMore()
    {
        var dipped = CollectIntents(ContextFor(availableCash: 50_000m, sharesOwned: 0, companyPrice: 100m, longRangeChangePct: -0.80m));
        var flat = CollectIntents(ContextFor(availableCash: 50_000m, sharesOwned: 0, companyPrice: 100m));

        Assert.All(dipped, intent => Assert.Equal(OrderType.Buy, intent.Type));
        Assert.True(dipped.Count > flat.Count, "a deep drop should pull non-owners into buying more often");
    }

    [Fact]
    public void HigherRiskPlacesOrdersOnMoreCyclesThanLowerRisk()
    {
        var high = CountOrders(Temperament.Balanced, RiskProfile.High);
        var medium = CountOrders(Temperament.Balanced, RiskProfile.Medium);
        var low = CountOrders(Temperament.Balanced, RiskProfile.Low);

        Assert.True(high > medium, $"high {high} should exceed medium {medium}");
        Assert.True(medium > low, $"medium {medium} should exceed low {low}");
    }

    [Fact]
    public void AggressiveTemperamentPlacesOrdersOnMoreCyclesThanConservative()
    {
        var aggressive = CountOrders(Temperament.Aggressive, RiskProfile.Medium);
        var conservative = CountOrders(Temperament.Conservative, RiskProfile.Medium);

        Assert.True(aggressive > conservative, $"aggressive {aggressive} should exceed conservative {conservative}");
    }

    [Fact]
    public void DebtPullsAnOwnerTowardSellingMoreOften()
    {
        var withDebt = CountSells(DebtorContext(loanLiability: 180m, sharesOwned: 10, companyPrice: 100m, RiskProfile.Low));
        var noDebt = CountSells(DebtorContext(loanLiability: 0m, sharesOwned: 10, companyPrice: 100m, RiskProfile.Low));

        Assert.True(withDebt > noDebt, $"debt {withDebt} should raise selling above no-debt {noDebt}");
    }

    [Fact]
    public void LowRiskDeleveragesHarderThanHighRisk()
    {
        // Isolate the debt-driven pull from the baseline personality by comparing each profile's own increase.
        var lowIncrease = CountSells(DebtorContext(180m, 10, 100m, RiskProfile.Low))
            - CountSells(DebtorContext(0m, 10, 100m, RiskProfile.Low));
        var highIncrease = CountSells(DebtorContext(180m, 10, 100m, RiskProfile.High))
            - CountSells(DebtorContext(0m, 10, 100m, RiskProfile.High));

        Assert.True(lowIncrease > highIncrease,
            $"low-risk debt selling increase {lowIncrease} should exceed high-risk {highIncrease}");
    }

    [Fact]
    public void DeeperDebtSellsMoreThanShallowDebt()
    {
        var deep = CountSells(DebtorContext(180m, 10, 100m, RiskProfile.Medium));
        var shallow = CountSells(DebtorContext(5m, 10, 100m, RiskProfile.Medium));

        Assert.True(deep > shallow, $"deep debt {deep} should sell more than shallow debt {shallow}");
    }

    [Fact]
    public void PositiveSectorSentimentPullsBuyWhileNeutralAndNegativeDoNot()
    {
        var positive = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.10d, 0.50d, 0d], []))
            .Decide(ContextFor(availableCash: 1_000m, sharesOwned: 0, companyPrice: 100m, sectorSentiment: 1_000));
        var neutral = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.10d], [0]))
            .Decide(ContextFor(availableCash: 1_000m, sharesOwned: 0, companyPrice: 100m, sectorSentiment: 0));
        var negative = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.10d], [0]))
            .Decide(ContextFor(availableCash: 1_000m, sharesOwned: 0, companyPrice: 100m, sectorSentiment: -1_000));

        Assert.Collection(positive, intent => Assert.Equal(OrderType.Buy, intent.Type));
        Assert.Empty(neutral);
        Assert.Empty(negative);
    }

    [Fact]
    public void NegativeSectorSentimentPullsSellWhileNeutralAndPositiveDoNot()
    {
        var negative = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.10d, 0.50d, 0d], [0]))
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, sectorSentiment: -1_000));
        var neutral = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.10d], [0]))
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, sectorSentiment: 0));
        var positive = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.10d], [0]))
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, sectorSentiment: 1_000));

        Assert.Collection(negative, intent => Assert.Equal(OrderType.Sell, intent.Type));
        Assert.Empty(neutral);
        Assert.Empty(positive);
    }

    [Fact]
    public void SectorSentimentClampsAtThePlusAndMinusThousandEquivalents()
    {
        var positiveAtLimit = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.19d, 0.50d, 0d], []))
            .Decide(ContextFor(availableCash: 1_000m, sharesOwned: 0, companyPrice: 100m, sectorSentiment: 1_000));
        var positiveBeyondLimit = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.20d], [0]))
            .Decide(ContextFor(availableCash: 1_000m, sharesOwned: 0, companyPrice: 100m, sectorSentiment: 5_000));
        var negativeAtLimit = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.19d, 0.50d, 0d], []))
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, sectorSentiment: -1_000));
        var negativeBeyondLimit = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.20d], [0]))
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, sectorSentiment: -5_000));

        Assert.Collection(positiveAtLimit, intent => Assert.Equal(OrderType.Buy, intent.Type));
        Assert.Empty(positiveBeyondLimit);
        Assert.Collection(negativeAtLimit, intent => Assert.Equal(OrderType.Sell, intent.Type));
        Assert.Empty(negativeBeyondLimit);
    }

    [Fact]
    public void SentimentSignalsRespectTheExistingBuyAndSellPullCaps()
    {
        var buy = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.81d], [0]))
            .Decide(ContextFor(
                availableCash: 1_000m,
                sharesOwned: 0,
                companyPrice: 100m,
                priceChangePct: 0.10m,
                netShareDemand: 200,
                longRangeChangePct: -0.80m,
                sectorSentiment: 1_000));
        var sell = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.81d], [0]))
            .Decide(ContextFor(
                availableCash: 0m,
                sharesOwned: 10,
                companyPrice: 100m,
                priceChangePct: -0.10m,
                longRangeChangePct: 0.80m,
                sectorSentiment: -1_000));

        Assert.Empty(buy);
        Assert.Empty(sell);
    }

    [Fact]
    public void SentimentDoesNotAddRandomDrawsWhenTheDecisionPathIsUnchanged()
    {
        var neutralRandom = new ScriptedRandom([0.90d], [0]);
        var positiveRandom = new ScriptedRandom([0.90d], [0]);

        var neutral = new RuleBasedDecisionEngine(new MaxTradeSizer(), Options.Create(new RandomChanceRatesOptions()), neutralRandom)
            .Decide(ContextFor(availableCash: 1_000m, sharesOwned: 0, companyPrice: 100m, sectorSentiment: 0));
        var positive = new RuleBasedDecisionEngine(new MaxTradeSizer(), Options.Create(new RandomChanceRatesOptions()), positiveRandom)
            .Decide(ContextFor(availableCash: 1_000m, sharesOwned: 0, companyPrice: 100m, sectorSentiment: 1_000));

        Assert.Empty(neutral);
        Assert.Empty(positive);
        Assert.Equal(neutralRandom.DoubleDrawCount, positiveRandom.DoubleDrawCount);
        Assert.Equal(neutralRandom.IntegerDrawCount, positiveRandom.IntegerDrawCount);
        Assert.Equal(1, positiveRandom.DoubleDrawCount);
        Assert.Equal(1, positiveRandom.IntegerDrawCount);
    }

    // Same fixed seed per engine so the only difference between compared runs is the debt/risk under test.
    private static int CountSells(DecisionContext context)
    {
        var engine = new RuleBasedDecisionEngine(new MaxTradeSizer(), Options.Create(new RandomChanceRatesOptions()), new Random(20260619));
        var sells = 0;
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            sells += engine.Decide(context).Count(intent => intent.Type == OrderType.Sell);
        }

        return sells;
    }

    // A trader carrying loan debt that still owns sellable shares, with no spare buying power.
    private static DecisionContext DebtorContext(decimal loanLiability, int sharesOwned, decimal companyPrice, RiskProfile riskProfile)
    {
        const int companyId = 1;
        var participant = new Participant
        {
            Name = "Debtor",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = riskProfile,
            InitialBalance = 0m,
            CurrentBalance = 0m,
            IsActive = true,
        };

        return new DecisionContext(
            participant,
            AvailableCash: 0m,
            [new CompanyQuote(companyId, companyPrice, Bounds: Bounds(companyPrice))],
            new Dictionary<int, int> { [companyId] = sharesOwned },
            new HashSet<int>(),
            LoanLiability: loanLiability);
    }

    // A flat, no-signal market with both cash and shares leaves every choice to the weighted fallback, so
    // the order count isolates how personality shifts trading frequency. A fresh, equally seeded engine per
    // profile keeps the only difference the profile itself.
    private static int CountOrders(Temperament temperament, RiskProfile riskProfile)
    {
        var engine = new RuleBasedDecisionEngine(new MaxTradeSizer(), Options.Create(new RandomChanceRatesOptions()), new Random(20260619));
        var context = ContextFor(
            availableCash: 5000m,
            sharesOwned: 10,
            companyPrice: 100m,
            temperament: temperament,
            riskProfile: riskProfile);

        var orders = 0;
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            orders += engine.Decide(context).Count;
        }

        return orders;
    }

    // Skip is always one of the choices, so the engine is exercised many times to surface the action
    // branch; the invariants must then hold across every outcome.
    private IReadOnlyList<OrderIntent> CollectIntents(DecisionContext context)
    {
        var intents = new List<OrderIntent>();
        for (var iteration = 0; iteration < 200; iteration++)
        {
            intents.AddRange(engine.Decide(context));
        }

        return intents;
    }

    private static DecisionContext ContextFor(
        decimal availableCash,
        int sharesOwned,
        decimal companyPrice,
        int[]? companiesWithOpenOrders = null,
        decimal priceChangePct = 0m,
        int netShareDemand = 0,
        decimal longRangeChangePct = 0m,
        int sectorSentiment = 0,
        Temperament temperament = Temperament.Balanced,
        RiskProfile riskProfile = RiskProfile.Medium)
    {
        const int companyId = 1;

        var holdings = sharesOwned > 0
            ? new Dictionary<int, int> { [companyId] = sharesOwned }
            : new Dictionary<int, int>();

        return new DecisionContext(
            NewParticipant(availableCash, temperament, riskProfile),
            availableCash,
            [new CompanyQuote(companyId, companyPrice, priceChangePct, netShareDemand, longRangeChangePct, sectorSentiment, Bounds(companyPrice))],
            holdings,
            new HashSet<int>(companiesWithOpenOrders ?? []));
    }

    // Bounds for the approved -15%/+10% band and -25%/+15% allowed range, attached to every test quote so the
    // engine treats the company as priceable.
    private static OrderPriceBounds Bounds(decimal price) =>
        OrderPriceBounds.FromReference(price, 15m, 10m, 25m, 15m);

    private static Participant NewParticipant(
        decimal availableCash,
        Temperament temperament = Temperament.Balanced,
        RiskProfile riskProfile = RiskProfile.Medium) => new()
    {
        Name = "Trader",
        Type = ParticipantType.Individual,
        Temperament = temperament,
        RiskProfile = riskProfile,
        InitialBalance = availableCash,
        CurrentBalance = availableCash,
        IsActive = true,
    };

    private sealed class ScriptedRandom(double[] doubles, int[] ints) : Random
    {
        private readonly Queue<double> doubles = new(doubles);
        private readonly Queue<int> ints = new(ints);

        public int DoubleDrawCount { get; private set; }

        public int IntegerDrawCount { get; private set; }

        public override double NextDouble()
        {
            DoubleDrawCount++;
            return doubles.Dequeue();
        }

        public override int Next(int maxValue)
        {
            IntegerDrawCount++;
            return ints.Dequeue();
        }
    }
}
