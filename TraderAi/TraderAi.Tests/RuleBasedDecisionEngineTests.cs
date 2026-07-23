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
    public void IndividualBelowTargetCanBuyInAFlatMarket()
    {
        var context = AutomatedContextFor(
            riskProfile: RiskProfile.Medium,
            netWorth: 10_000m,
            holdingsValue: 0m,
            availableBalance: 10_000m,
            buyingPower: 10_000m,
            bestAskPrice: 100m,
            bestAskQuantity: 100,
            issuedShares: 10_000);
        var assessment = EngineWith([0.99d]).Evaluate(context);

        var intents = EngineWith([(double)(assessment.Probabilities.Buy / 2m)])
            .Decide(context);

        Assert.Collection(intents, intent => Assert.Equal(OrderType.Buy, intent.Type));
    }

    [Fact]
    public void IndividualBelowTargetUsesStableTieBreakingForFlatExecutableCandidates()
    {
        var participant = NewParticipant(100_000m, riskProfile: RiskProfile.Medium);
        var context = new DecisionContext(
            participant,
            AvailableCash: 100_000m,
            [
                new CompanyQuote(
                    1,
                    Price: 100m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000,
                    BestExecutableSellPrice: 101m,
                    BestExecutableSellQuantity: 5),
                new CompanyQuote(
                    2,
                    Price: 100m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000,
                    BestExecutableSellPrice: 102m,
                    BestExecutableSellQuantity: 6),
            ],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            HoldingsValue: 0m,
            NetWorth: 100_000m,
            AvailableBalance: 100_000m,
            BuyingPower: 100_000m,
            HasAutomatedTradingData: true);
        var random = new ScriptedRandom([0.05d], []);

        var intent = Assert.Single(new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            random).Decide(context));

        Assert.Equal(1, intent.CompanyId);
        Assert.Equal(101m, intent.LimitPrice);
        Assert.Equal(0, random.IntegerDrawCount);
        Assert.Equal(0, random.RemainingIntegerDraws);
    }

    [Fact]
    public void IndividualAtOrAboveMaximumExposureNeverBuys()
    {
        foreach (var holdingsValue in new[] { 5_500m, 6_000m })
        {
            var intents = EngineWith([0.05d, 0.50d, 0d], [0])
                .Decide(AutomatedContextFor(
                    riskProfile: RiskProfile.Medium,
                    netWorth: 10_000m,
                    holdingsValue: holdingsValue,
                    availableBalance: 10_000m - holdingsValue,
                    buyingPower: 10_000m - holdingsValue,
                    bestAskPrice: 100m,
                    bestAskQuantity: 100,
                    issuedShares: 10_000,
                    priceChangePct: 0.10m));

            Assert.DoesNotContain(intents, intent => intent.Type == OrderType.Buy);
        }
    }

    [Fact]
    public void IndividualBelowTargetCanStillSkip()
    {
        var intents = EngineWith([0.90d], [0, 0])
            .Decide(AutomatedContextFor(
                riskProfile: RiskProfile.Medium,
                netWorth: 10_000m,
                holdingsValue: 0m,
                availableBalance: 10_000m,
                buyingPower: 10_000m,
                bestAskPrice: 100m,
                bestAskQuantity: 100,
                issuedShares: 10_000));

        Assert.Empty(intents);
    }

    [Fact]
    public void IndividualBelowTargetPrefersExecutableSupplyOverAStrongerPassiveSignal()
    {
        var participant = NewParticipant(100_000m, riskProfile: RiskProfile.Medium);
        var context = new DecisionContext(
            participant,
            AvailableCash: 100_000m,
            [
                new CompanyQuote(
                    1,
                    Price: 100m,
                    PriceChangePct: 0.10m,
                    OrderFlowImbalance: 1m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000),
                new CompanyQuote(
                    2,
                    Price: 100m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000,
                    BestExecutableSellPrice: 103m,
                    BestExecutableSellQuantity: 5),
                new CompanyQuote(
                    3,
                    Price: 100m,
                    PriceChangePct: 0.02m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000,
                    BestExecutableSellPrice: 104m,
                    BestExecutableSellQuantity: 4),
            ],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            HoldingsValue: 0m,
            NetWorth: 100_000m,
            AvailableBalance: 100_000m,
            BuyingPower: 100_000m,
            HasAutomatedTradingData: true);
        var random = new ScriptedRandom([0.05d], []);

        var intent = Assert.Single(new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            random).Decide(context));

        Assert.Equal(3, intent.CompanyId);
        Assert.Equal(104m, intent.LimitPrice);
        Assert.Equal(4, intent.Quantity);
        Assert.Equal(0, random.IntegerDrawCount);
        Assert.Equal(0, random.RemainingIntegerDraws);
    }

    [Fact]
    public void IndividualBelowTargetStableTieBreakExcludesPassiveOnlyCandidates()
    {
        var participant = NewParticipant(100_000m, riskProfile: RiskProfile.Medium);
        var context = new DecisionContext(
            participant,
            AvailableCash: 100_000m,
            [
                new CompanyQuote(
                    1,
                    Price: 100m,
                    PriceChangePct: 0.10m,
                    OrderFlowImbalance: 1m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000),
                new CompanyQuote(
                    2,
                    Price: 100m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000,
                    BestExecutableSellPrice: 103m,
                    BestExecutableSellQuantity: 5),
                new CompanyQuote(
                    3,
                    Price: 100m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000,
                    BestExecutableSellPrice: 104m,
                    BestExecutableSellQuantity: 4),
            ],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            HoldingsValue: 0m,
            NetWorth: 100_000m,
            AvailableBalance: 100_000m,
            BuyingPower: 100_000m,
            HasAutomatedTradingData: true);

        var intent = Assert.Single(EngineWith([0.05d]).Decide(context));

        Assert.Equal(2, intent.CompanyId);
        Assert.Equal(103m, intent.LimitPrice);
    }

    [Fact]
    public void IndividualBelowTargetUsesStableTieBreakWhenNoAskIsExecutable()
    {
        var participant = NewParticipant(100_000m, riskProfile: RiskProfile.Medium);
        var context = new DecisionContext(
            participant,
            AvailableCash: 100_000m,
            [
                new CompanyQuote(1, Price: 100m, Bounds: Bounds(100m), IssuedShares: 10_000),
                new CompanyQuote(2, Price: 100m, Bounds: Bounds(100m), IssuedShares: 10_000),
            ],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            HoldingsValue: 0m,
            NetWorth: 100_000m,
            AvailableBalance: 100_000m,
            BuyingPower: 100_000m,
            HasAutomatedTradingData: true);

        var intent = Assert.Single(EngineWith([0.05d, 0d]).Decide(context));

        Assert.Equal(1, intent.CompanyId);
        Assert.True(Bounds(100m).IsWithinActiveBand(intent.LimitPrice));
    }

    [Fact]
    public void IndividualBuyCrossesTheBestAskAndCapsToItsAvailableQuantity()
    {
        var intents = EngineWith([0.05d, 0.50d, 0d])
            .Decide(AutomatedContextFor(
                riskProfile: RiskProfile.Medium,
                netWorth: 100_000m,
                holdingsValue: 0m,
                availableBalance: 100_000m,
                buyingPower: 100_000m,
                bestAskPrice: 105m,
                bestAskQuantity: 3,
                issuedShares: 10_000,
                priceChangePct: 0.10m));

        Assert.Collection(intents, intent =>
        {
            Assert.Equal(105m, intent.LimitPrice);
            Assert.Equal(3, intent.Quantity);
        });
    }

    // An executable ask removes pricing randomness and stable target selection consumes no additional draw.
    [Fact]
    public void IndividualBestAskBuyDrawsOnlyTheDecisionRoll()
    {
        var random = new ScriptedRandom([0.05d], []);
        var engine = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            random);

        var intent = Assert.Single(engine.Decide(AutomatedContextFor(
            riskProfile: RiskProfile.Medium,
            netWorth: 100_000m,
            holdingsValue: 0m,
            availableBalance: 100_000m,
            buyingPower: 100_000m,
            bestAskPrice: 105m,
            bestAskQuantity: 3,
            issuedShares: 10_000)));

        Assert.Equal(105m, intent.LimitPrice);
        Assert.Equal(1, random.DoubleDrawCount);
        Assert.Equal(0, random.IntegerDrawCount);
        Assert.Equal(0, random.RemainingDoubleDraws);
        Assert.Equal(0, random.RemainingIntegerDraws);
    }

    [Fact]
    public void IndividualWithoutASellerUsesAnInsideBandPassiveCap()
    {
        var bounds = Bounds(100m);
        var intents = EngineWith([0.05d, 0.05d, 0d])
            .Decide(AutomatedContextFor(
                riskProfile: RiskProfile.High,
                netWorth: 100_000m,
                holdingsValue: 0m,
                availableBalance: 100_000m,
                buyingPower: 100_000m,
                bestAskPrice: null,
                bestAskQuantity: 0,
                issuedShares: 10_000,
                priceChangePct: 0.10m));

        Assert.Collection(intents, intent =>
        {
            Assert.True(bounds.IsWithinActiveBand(intent.LimitPrice));
            Assert.Equal(25, intent.Quantity);
        });
    }

    // Stable target selection consumes no draw; only the action and automated passive premium are sampled.
    [Fact]
    public void IndividualPassiveBuyDrawsDecisionAndPremiumOnly()
    {
        var random = new ScriptedRandom([0.05d, 0d], []);
        var engine = new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            random);

        var intent = Assert.Single(engine.Decide(AutomatedContextFor(
            riskProfile: RiskProfile.High,
            netWorth: 100_000m,
            holdingsValue: 0m,
            availableBalance: 100_000m,
            buyingPower: 100_000m,
            bestAskPrice: null,
            bestAskQuantity: 0,
            issuedShares: 10_000)));

        Assert.True(Bounds(100m).IsWithinActiveBand(intent.LimitPrice));
        Assert.Equal(2, random.DoubleDrawCount);
        Assert.Equal(0, random.IntegerDrawCount);
        Assert.Equal(0, random.RemainingDoubleDraws);
        Assert.Equal(0, random.RemainingIntegerDraws);
    }

    [Fact]
    public void IndividualWithoutASellerUsesTheConfiguredBuyChanceAsProbabilityWeight()
    {
        var chanceRates = new RandomChanceRatesOptions();
        chanceRates.EventTriggerChances.NoSellOrderBuyChance = 0.65;
        var context = AutomatedContextFor(
            riskProfile: RiskProfile.Medium,
            netWorth: 100_000m,
            holdingsValue: 0m,
            availableBalance: 100_000m,
            buyingPower: 100_000m,
            bestAskPrice: null,
            bestAskQuantity: 0,
            issuedShares: 10_000);

        var configured = EngineWith([0.99d], chanceRates: chanceRates)
            .Evaluate(context);
        chanceRates.EventTriggerChances.NoSellOrderBuyChance = 0.30;
        var reduced = EngineWith([0.99d], chanceRates: chanceRates)
            .Evaluate(context);

        Assert.True(configured.Probabilities.Buy > reduced.Probabilities.Buy);
    }

    [Theory]
    [InlineData(Temperament.Conservative, 103.33)]
    [InlineData(Temperament.Balanced, 108.00)]
    [InlineData(Temperament.Aggressive, 112.67)]
    public void IndividualPassiveBuyPremiumIncreasesWithTemperament(
        Temperament temperament,
        decimal expectedLimitPrice)
    {
        var context = AutomatedContextFor(
            riskProfile: RiskProfile.Medium,
            netWorth: 100_000m,
            holdingsValue: 0m,
            availableBalance: 100_000m,
            buyingPower: 100_000m,
            bestAskPrice: null,
            bestAskQuantity: 0,
            issuedShares: 10_000,
            temperament: temperament,
            bounds: OrderPriceBounds.FromReference(100m, 15m, 20m, 25m, 25m));

        var assessment = EngineWith([0.99d]).Evaluate(context);
        var intent = Assert.Single(EngineWith(
            [(double)(assessment.Probabilities.Buy / 2m), 0.50d]).Decide(context));

        Assert.Equal(expectedLimitPrice, intent.LimitPrice);
    }

    [Fact]
    public void IndividualDoesNotTreatANonExecutableOpenSellAsAbsentSupply()
    {
        var context = AutomatedContextFor(
            riskProfile: RiskProfile.Medium,
            netWorth: 100_000m,
            holdingsValue: 0m,
            availableBalance: 100_000m,
            buyingPower: 100_000m,
            bestAskPrice: null,
            bestAskQuantity: 0,
            issuedShares: 10_000,
            priceChangePct: 0.10m,
            openSellQuantity: 5);

        var intents = EngineWith([0.05d, 0.50d, 0d], [0]).Decide(context);

        Assert.Empty(intents);
    }

    [Fact]
    public void IndividualDoesNotPlaceAPassiveBidWhenTheBandHasNoPriceAboveMarket()
    {
        var context = AutomatedContextFor(
            riskProfile: RiskProfile.Medium,
            netWorth: 100_000m,
            holdingsValue: 0m,
            availableBalance: 100_000m,
            buyingPower: 100_000m,
            bestAskPrice: null,
            bestAskQuantity: 0,
            issuedShares: 10_000,
            bounds: new OrderPriceBounds(100m, 85m, 100m, 75m, 115m));

        var intents = EngineWith([0.05d, 0.50d], [0]).Decide(context);

        Assert.Empty(intents);
    }

    [Fact]
    public void IndividualPlacesNoPassiveBidWhenTheCompanyIsFalling()
    {
        var context = AutomatedContextFor(
            riskProfile: RiskProfile.High,
            netWorth: 100_000m,
            holdingsValue: 0m,
            availableBalance: 100_000m,
            buyingPower: 100_000m,
            bestAskPrice: null,
            bestAskQuantity: 0,
            issuedShares: 10_000,
            longRangeChangePct: -0.10m);

        var intents = EngineWith([0.5d], [0]).Decide(context);

        Assert.Empty(intents);
    }

    [Fact]
    public void IndividualPlacesAnAboveMarketPassiveBidWhenARisingCompanyHasNoSeller()
    {
        var context = AutomatedContextFor(
            riskProfile: RiskProfile.High,
            netWorth: 100_000m,
            holdingsValue: 0m,
            availableBalance: 100_000m,
            buyingPower: 100_000m,
            bestAskPrice: null,
            bestAskQuantity: 0,
            issuedShares: 10_000,
            longRangeChangePct: 0.20m);

        var intent = Assert.Single(EngineWith([0.05d, 0d], [0]).Decide(context));

        Assert.Equal(OrderType.Buy, intent.Type);
        Assert.True(intent.LimitPrice > 100m);
        Assert.True(Bounds(100m).IsWithinActiveBand(intent.LimitPrice));
    }

    [Fact]
    public void ADecidedBuyFansOutToDistinctCompaniesUpToTheConfiguredCount()
    {
        var participant = NewParticipant(100_000m, riskProfile: RiskProfile.High);
        var context = new DecisionContext(
            participant,
            AvailableCash: 100_000m,
            [
                new CompanyQuote(1, Price: 100m, Bounds: Bounds(100m), IssuedShares: 10_000,
                    BestExecutableSellPrice: 100m, BestExecutableSellQuantity: 50, OpenSellQuantity: 50),
                new CompanyQuote(2, Price: 100m, Bounds: Bounds(100m), IssuedShares: 10_000,
                    BestExecutableSellPrice: 100m, BestExecutableSellQuantity: 50, OpenSellQuantity: 50),
                new CompanyQuote(3, Price: 100m, Bounds: Bounds(100m), IssuedShares: 10_000,
                    BestExecutableSellPrice: 100m, BestExecutableSellQuantity: 50, OpenSellQuantity: 50),
            ],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            HoldingsValue: 0m,
            NetWorth: 100_000m,
            AvailableBalance: 100_000m,
            BuyingPower: 100_000m,
            HasAutomatedTradingData: true);
        var options = new AutomatedTradingOptions { BuyOrdersPerCycleMin = 3, BuyOrdersPerCycleMax = 3 };

        var intents = EngineWith([0.1d], [0], automatedTradingOptions: options).Decide(context);

        Assert.Equal(3, intents.Count);
        Assert.All(intents, intent => Assert.Equal(OrderType.Buy, intent.Type));
        Assert.Equal(3, intents.Select(intent => intent.CompanyId).Distinct().Count());
    }

    [Fact]
    public void BelowTargetFanoutKeepsExecutableSupplyPreference()
    {
        var participant = NewParticipant(100_000m, riskProfile: RiskProfile.Medium);
        var context = new DecisionContext(
            participant,
            AvailableCash: 100_000m,
            [
                new CompanyQuote(
                    1,
                    Price: 100m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000,
                    BestExecutableSellPrice: 101m,
                    BestExecutableSellQuantity: 5,
                    OpenSellQuantity: 5),
                new CompanyQuote(
                    2,
                    Price: 100m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000),
            ],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            HoldingsValue: 0m,
            NetWorth: 100_000m,
            AvailableBalance: 100_000m,
            BuyingPower: 100_000m,
            HasAutomatedTradingData: true);
        var options = new AutomatedTradingOptions
        {
            BuyOrdersPerCycleMin = 2,
            BuyOrdersPerCycleMax = 2,
        };
        var assessment = EngineWith([0.99d], automatedTradingOptions: options)
            .Evaluate(context);

        var intent = Assert.Single(EngineWith(
            [(double)(assessment.Probabilities.Buy / 2m), 0.50d],
            automatedTradingOptions: options).Decide(context));

        Assert.Equal(1, intent.CompanyId);
        Assert.Equal(101m, intent.LimitPrice);
    }

    [Fact]
    public void IndividualUniformFallbackAppliesThePassiveBuyChance()
    {
        var chanceRates = new RandomChanceRatesOptions();
        chanceRates.EventTriggerChances.NoSellOrderBuyChance = 0d;
        var participant = NewParticipant(60_000m, riskProfile: RiskProfile.Medium);
        var context = new DecisionContext(
            participant,
            AvailableCash: 60_000m,
            [
                new CompanyQuote(
                    1,
                    Price: 100m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000,
                    BestExecutableSellPrice: 101m,
                    BestExecutableSellQuantity: 5,
                    OpenSellQuantity: 5),
                new CompanyQuote(
                    2,
                    Price: 100m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000),
            ],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            HoldingsValue: 40_000m,
            NetWorth: 100_000m,
            AvailableBalance: 60_000m,
            BuyingPower: 60_000m,
            HasAutomatedTradingData: true);

        var intents = EngineWith([0.90d, 0d], [1, 1], chanceRates: chanceRates).Decide(context);

        Assert.Empty(intents);
    }

    [Fact]
    public void RejectedPassiveSignalKeepsExecutableFallbackCandidates()
    {
        var chanceRates = new RandomChanceRatesOptions();
        chanceRates.EventTriggerChances.NoSellOrderBuyChance = 0d;
        var participant = NewParticipant(60_000m, riskProfile: RiskProfile.Medium);
        var context = new DecisionContext(
            participant,
            AvailableCash: 60_000m,
            [
                new CompanyQuote(
                    1,
                    Price: 100m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000,
                    BestExecutableSellPrice: 101m,
                    BestExecutableSellQuantity: 5,
                    OpenSellQuantity: 5),
                new CompanyQuote(
                    2,
                    Price: 100m,
                    PriceChangePct: 0.10m,
                    Bounds: Bounds(100m),
                    IssuedShares: 10_000),
            ],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            HoldingsValue: 40_000m,
            NetWorth: 100_000m,
            AvailableBalance: 60_000m,
            BuyingPower: 60_000m,
            HasAutomatedTradingData: true);

        var intent = Assert.Single(
            EngineWith([0d, 0.90d], [1, 0], chanceRates: chanceRates).Decide(context));

        Assert.Equal(1, intent.CompanyId);
        Assert.Equal(101m, intent.LimitPrice);
    }

    [Fact]
    public void IndividualBelowTargetEnforcesTheMeaningfulMinimumQuantity()
    {
        var engine = new RuleBasedDecisionEngine(
            new OneTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            new ScriptedRandom([0.05d, 0.50d, 0d], []));

        var intents = engine.Decide(AutomatedContextFor(
            riskProfile: RiskProfile.Medium,
            netWorth: 1_000_000m,
            holdingsValue: 0m,
            availableBalance: 1_000_000m,
            buyingPower: 1_000_000m,
            bestAskPrice: 100m,
            bestAskQuantity: 100,
            issuedShares: 100_000,
            priceChangePct: 0.10m));

        Assert.Equal(25, Assert.Single(intents).Quantity);
    }

    [Theory]
    [InlineData(RiskProfile.Low)]
    [InlineData(RiskProfile.Medium)]
    public void LowAndMediumRiskIndividualsCannotSpendMarginBuyingPower(RiskProfile riskProfile)
    {
        var intent = Assert.Single(EngineWith([0.05d, 0.50d, 0d])
            .Decide(AutomatedContextFor(
                riskProfile: riskProfile,
                netWorth: 1_000_000m,
                holdingsValue: 0m,
                availableBalance: 1_000m,
                buyingPower: 100_000m,
                bestAskPrice: 100m,
                bestAskQuantity: 1_000,
                issuedShares: 100_000,
                priceChangePct: 0.10m)));

        Assert.True(intent.Quantity * intent.LimitPrice <= 1_000m);
    }

    [Fact]
    public void HighRiskIndividualMarginUseIsBoundedByThePolicy()
    {
        var intent = Assert.Single(EngineWith([0.05d, 0.50d, 0d])
            .Decide(AutomatedContextFor(
                riskProfile: RiskProfile.High,
                netWorth: 100_000m,
                holdingsValue: 0m,
                availableBalance: 1_000m,
                buyingPower: 100_000m,
                bestAskPrice: 100m,
                bestAskQuantity: 1_000,
                issuedShares: 100_000,
                priceChangePct: 0.10m)));

        Assert.Equal(3_000m, intent.Quantity * intent.LimitPrice);
    }

    [Fact]
    public void CollectiveFundUsesSymmetricInsideBandPricingAndLegacySizing()
    {
        var participant = NewParticipant(5_000m);
        participant.Type = ParticipantType.CollectiveFund;
        var context = new DecisionContext(
            participant,
            AvailableCash: 5_000m,
            [new CompanyQuote(
                1,
                Price: 100m,
                PriceChangePct: 0.10m,
                Bounds: Bounds(100m),
                IssuedShares: 10_000,
                BestExecutableSellPrice: 100m,
                BestExecutableSellQuantity: 2)],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            HoldingsValue: 0m,
            NetWorth: 5_000m,
            AvailableBalance: 5_000m,
            BuyingPower: 5_000m,
            HasAutomatedTradingData: true);

        var intent = Assert.Single(EngineWith([0.05d, 0.05d]).Decide(context));

        Assert.Equal(95.40m, intent.LimitPrice);
        Assert.Equal(52, intent.Quantity);
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
            [new CompanyQuote(1, Price: 100m, PriceChangePct: 0.10m, OrderFlowImbalance: 1m, Bounds: Bounds(100m))],
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

    // The action roll is followed by one symmetric price-position draw inside the active band.
    [Fact]
    public void MidpointPassiveBuySitsAtTheReference()
    {
        var buy = EngineWith([0.05d, 0.50d])
            .Decide(ContextFor(availableCash: 5000m, sharesOwned: 0, companyPrice: 100m, priceChangePct: 0.10m));

        Assert.Collection(buy, intent =>
        {
            Assert.Equal(OrderType.Buy, intent.Type);
            Assert.Equal(100m, intent.LimitPrice);
        });
    }

    [Fact]
    public void RaisedBandReferencePullsInsideBuyFormationAboveTheLatestTrade()
    {
        var bounds = OrderPriceBounds.FromReference(110m, 15m, 10m, 25m, 15m);
        var buy = EngineWith([0.05d, 0.50d])
            .Decide(ContextFor(
                availableCash: 5000m,
                sharesOwned: 0,
                companyPrice: 100m,
                priceChangePct: 0.10m,
                bounds: bounds));

        Assert.Collection(buy, intent =>
        {
            Assert.Equal(OrderType.Buy, intent.Type);
            Assert.Equal(110m, intent.LimitPrice);
        });
    }

    // Rule-based sells spread symmetrically around the reference instead of undercutting it: a mid draw rests
    // exactly at the market and the upper half of the draw range places the ask above it.
    [Fact]
    public void InsideBandSellSpreadsSymmetricallyAroundTheReference()
    {
        var atReference = EngineWith([0.05d, 0.50d])
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, priceChangePct: -0.10m));

        Assert.Collection(atReference, intent =>
        {
            Assert.Equal(OrderType.Sell, intent.Type);
            Assert.Equal(100m, intent.LimitPrice);
        });

        var aboveReference = EngineWith([0.05d, 0.90d])
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, priceChangePct: -0.10m));

        Assert.Collection(aboveReference, intent =>
        {
            Assert.Equal(OrderType.Sell, intent.Type);
            Assert.Equal(104.20m, intent.LimitPrice);
        });
    }

    [Fact]
    public void LowestPassiveBuySampleStaysInsideTheActiveBand()
    {
        var bounds = Bounds(100m);
        var buy = EngineWith([0.05d, 0d])
            .Decide(ContextFor(availableCash: 5000m, sharesOwned: 0, companyPrice: 100m, priceChangePct: 0.10m));

        Assert.Collection(buy, intent =>
        {
            Assert.Equal(OrderType.Buy, intent.Type);
            Assert.Equal(95m, intent.LimitPrice);
            Assert.True(bounds.IsWithinActiveBand(intent.LimitPrice));
        });
    }

    [Fact]
    public void HighestPassiveSellSampleStaysInsideTheActiveBand()
    {
        var bounds = Bounds(100m);
        var sell = EngineWith([0.05d, 1d])
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, priceChangePct: -0.10m));

        Assert.Collection(sell, intent =>
        {
            Assert.Equal(OrderType.Sell, intent.Type);
            Assert.Equal(105m, intent.LimitPrice);
            Assert.True(bounds.IsWithinActiveBand(intent.LimitPrice));
        });
    }

    [Fact]
    public void BothSidesCanRestOnEitherSideOfTheReferenceInsideTheBand()
    {
        var bounds = Bounds(100m);

        var buyLower = EngineWith([0.05d, 0d])
            .Decide(ContextFor(availableCash: 5000m, sharesOwned: 0, companyPrice: 100m, priceChangePct: 0.10m));
        var buyUpper = EngineWith([0.05d, 1d])
            .Decide(ContextFor(availableCash: 5000m, sharesOwned: 0, companyPrice: 100m, priceChangePct: 0.10m));
        var sellLower = EngineWith([0.05d, 0d])
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, priceChangePct: -0.10m));
        var sellUpper = EngineWith([0.05d, 1d])
            .Decide(ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m, priceChangePct: -0.10m));

        Assert.Equal(OrderType.Buy, buyLower.Single().Type);
        Assert.True(buyLower.Single().LimitPrice < bounds.ReferencePrice);
        Assert.True(buyUpper.Single().LimitPrice > bounds.ReferencePrice);
        Assert.True(sellLower.Single().LimitPrice < bounds.ReferencePrice);
        Assert.True(sellUpper.Single().LimitPrice > bounds.ReferencePrice);
        Assert.All(
            new[] { buyLower, buyUpper, sellLower, sellUpper },
            intents => Assert.True(bounds.IsWithinActiveBand(intents.Single().LimitPrice)));
    }

    [Fact]
    public void NoGeneratedPriceEverLeavesTheActiveBand()
    {
        var bounds = Bounds(100m);
        var context = ContextFor(availableCash: 5000m, sharesOwned: 10, companyPrice: 100m);

        Assert.All(CollectIntents(context), intent =>
            Assert.True(bounds.IsWithinActiveBand(intent.LimitPrice), $"price {intent.LimitPrice} left the active band"));
    }

    // One passive order draws exactly two doubles: the action distribution and symmetric price position.
    [Fact]
    public void APricedOrderExhaustsExactlyTheScriptedDrawOrder()
    {
        var random = new ScriptedRandom([0.05d, 0.50d], []);
        var intents = new RuleBasedDecisionEngine(new MaxTradeSizer(), Options.Create(new RandomChanceRatesOptions()), random)
            .Decide(ContextFor(availableCash: 5000m, sharesOwned: 0, companyPrice: 100m, priceChangePct: 0.10m));

        Assert.Single(intents);
        Assert.Equal(2, random.DoubleDrawCount);
        Assert.Equal(0, random.IntegerDrawCount);
    }

    private static RuleBasedDecisionEngine EngineWith(
        double[] doubles,
        int[]? ints = null,
        AutomatedTradingOptions? automatedTradingOptions = null,
        RandomChanceRatesOptions? chanceRates = null,
        TradingSignalOptions? tradingSignalOptions = null) =>
        new(
            new MaxTradeSizer(),
            Options.Create(chanceRates ?? new RandomChanceRatesOptions()),
            new ScriptedRandom(doubles, ints ?? []),
            automatedTradingOptions: Options.Create(automatedTradingOptions ?? new AutomatedTradingOptions()),
            tradingSignalOptions: Options.Create(tradingSignalOptions ?? new TradingSignalOptions()));

    [Fact]
    public void DirectionalEvidenceIsScaleInvariantAndMirrorsMomentum()
    {
        var engine = EngineWith([0.99d]);
        var smallBook = ContextWithQuote(new CompanyQuote(
            1,
            Price: 100m,
            PriceChangePct: 0.04m,
            OrderFlowImbalance: (75m - 25m) / (75m + 25m),
            Bounds: Bounds(100m)));
        var scaledBook = ContextWithQuote(new CompanyQuote(
            1,
            Price: 100m,
            PriceChangePct: 0.04m,
            OrderFlowImbalance: (750_000m - 250_000m) / (750_000m + 250_000m),
            Bounds: Bounds(100m)));
        var mirrored = ContextWithQuote(new CompanyQuote(
            1,
            Price: 100m,
            PriceChangePct: -0.04m,
            OrderFlowImbalance: -0.50m,
            Bounds: Bounds(100m)));

        var positive = engine.Evaluate(smallBook).DirectionalScores[1];
        var scaled = engine.Evaluate(scaledBook).DirectionalScores[1];
        var negative = engine.Evaluate(mirrored).DirectionalScores[1];

        Assert.Equal(positive, scaled);
        Assert.Equal(positive, -negative);
        Assert.InRange(positive, 0m, 1m);
    }

    [Theory]
    [InlineData(CompanyRiskRating.RaisedExpectations, CompanyRiskRating.LowRisk)]
    [InlineData(CompanyRiskRating.ExtraRaisedExpectations, CompanyRiskRating.HighRisk)]
    public void PositiveAndNegativeAuditStatusesHaveSymmetricConfiguredStrength(
        CompanyRiskRating positiveRating,
        CompanyRiskRating negativeRating)
    {
        var engine = EngineWith([0.99d]);
        var positive = engine.Evaluate(ContextWithQuote(new CompanyQuote(
            1,
            100m,
            Bounds: Bounds(100m),
            Audit: Audit(positiveRating)))).DirectionalScores[1];
        var negative = engine.Evaluate(ContextWithQuote(new CompanyQuote(
            1,
            100m,
            Bounds: Bounds(100m),
            Audit: Audit(negativeRating)))).DirectionalScores[1];

        Assert.Equal(positive, -negative);
        Assert.True(positive > 0m);
    }

    [Fact]
    public void FundamentalsChangeDirectionWithoutRemovingWait()
    {
        var engine = EngineWith([0.99d]);
        var strong = engine.Evaluate(ContextWithQuote(new CompanyQuote(
            1,
            100m,
            Bounds: Bounds(100m),
            Financials: Financial(
                profitability: 90m,
                stability: 90m,
                closureRisk: 10m,
                outlook: ManagementOutlook.Positive,
                coverage: 2m))));
        var weak = engine.Evaluate(ContextWithQuote(new CompanyQuote(
            1,
            100m,
            Bounds: Bounds(100m),
            Financials: Financial(
                profitability: 10m,
                stability: 10m,
                closureRisk: 90m,
                outlook: ManagementOutlook.Negative,
                coverage: 0m))));

        Assert.True(strong.DirectionalScores[1] > 0m);
        Assert.True(weak.DirectionalScores[1] < 0m);
        Assert.True(strong.Probabilities.Wait > 0m);
        Assert.True(weak.Probabilities.Wait > 0m);
    }

    [Fact]
    public void RiskProfilesInterpretQualityAndGrowthWithoutChangingActionAvailability()
    {
        var quality = new CompanyQuote(
            1,
            100m,
            Bounds: Bounds(100m),
            Financials: Financial(
                profitability: 85m,
                stability: 95m,
                closureRisk: 5m,
                outlook: ManagementOutlook.Neutral,
                coverage: 2m));
        var volatileGrowth = new CompanyQuote(
            1,
            100m,
            Bounds: Bounds(100m),
            Financials: Financial(
                profitability: 65m,
                stability: 20m,
                closureRisk: 55m,
                outlook: ManagementOutlook.Positive,
                coverage: 1m));
        var lowRiskQuality = EngineWith([0.99d]).Evaluate(ContextWithQuote(
            quality,
            temperament: Temperament.Conservative,
            riskProfile: RiskProfile.Low));
        var lowRiskGrowth = EngineWith([0.99d]).Evaluate(ContextWithQuote(
            volatileGrowth,
            temperament: Temperament.Conservative,
            riskProfile: RiskProfile.Low));
        var highRiskGrowth = EngineWith([0.99d]).Evaluate(ContextWithQuote(
            volatileGrowth,
            temperament: Temperament.Aggressive,
            riskProfile: RiskProfile.High));

        Assert.True(lowRiskQuality.DirectionalScores[1] > lowRiskGrowth.DirectionalScores[1]);
        Assert.True(highRiskGrowth.DirectionalScores[1] > 0m);
        Assert.True(highRiskGrowth.Probabilities.Buy > 0m);
        Assert.True(highRiskGrowth.Probabilities.Wait > 0m);
    }

    [Fact]
    public void FinalProbabilitiesNormalizeAndRemoveUnavailableActions()
    {
        var buyOnly = EngineWith([0.99d]).Evaluate(ContextFor(
            availableCash: 5_000m,
            sharesOwned: 0,
            companyPrice: 100m));
        var sellOnly = EngineWith([0.99d]).Evaluate(ContextFor(
            availableCash: 0m,
            sharesOwned: 10,
            companyPrice: 100m));
        var neither = EngineWith([0.99d]).Evaluate(ContextFor(
            availableCash: 0m,
            sharesOwned: 0,
            companyPrice: 100m));

        AssertDistribution(buyOnly.Probabilities);
        Assert.Equal(0m, buyOnly.Probabilities.Sell);
        AssertDistribution(sellOnly.Probabilities);
        Assert.Equal(0m, sellOnly.Probabilities.Buy);
        Assert.Equal(new TradeActionProbabilities(0m, 0m, 1m), neither.Probabilities);
    }

    [Fact]
    public void NeutralEvidenceTreatsBuyAndSellEquallyAndOneRollSamplesSell()
    {
        var context = ContextFor(
            availableCash: 5_000m,
            sharesOwned: 10,
            companyPrice: 100m);
        var assessment = EngineWith([0.99d]).Evaluate(context);
        var sellRoll = (double)(assessment.Probabilities.Buy + (assessment.Probabilities.Sell / 2m));
        var random = new ScriptedRandom([sellRoll, 0.50d], []);
        var intent = Assert.Single(new RuleBasedDecisionEngine(
            new MaxTradeSizer(),
            Options.Create(new RandomChanceRatesOptions()),
            random,
            tradingSignalOptions: Options.Create(new TradingSignalOptions())).Decide(context));

        Assert.Equal(assessment.Probabilities.Buy, assessment.Probabilities.Sell);
        Assert.Equal(OrderType.Sell, intent.Type);
        Assert.Equal(2, random.DoubleDrawCount);
        Assert.Equal(0, random.IntegerDrawCount);
    }

    [Fact]
    public void PersonalityAndDebtAdjustActionsWithoutChangingDirectionalEvidence()
    {
        var neutralQuote = new CompanyQuote(1, 100m, Bounds: Bounds(100m));
        var conservativeContext = ContextWithQuote(
            neutralQuote,
            sharesOwned: 10,
            loanLiability: 0m,
            temperament: Temperament.Conservative,
            riskProfile: RiskProfile.Low);
        var aggressiveContext = ContextWithQuote(
            neutralQuote,
            sharesOwned: 10,
            loanLiability: 0m,
            temperament: Temperament.Aggressive,
            riskProfile: RiskProfile.High);
        var debtorContext = conservativeContext with { LoanLiability = 400m };

        var conservative = EngineWith([0.99d]).Evaluate(conservativeContext);
        var aggressive = EngineWith([0.99d]).Evaluate(aggressiveContext);
        var debtor = EngineWith([0.99d]).Evaluate(debtorContext);

        Assert.Equal(conservative.DirectionalScores[1], aggressive.DirectionalScores[1]);
        Assert.Equal(conservative.DirectionalScores[1], debtor.DirectionalScores[1]);
        Assert.True(aggressive.Probabilities.Wait < conservative.Probabilities.Wait);
        Assert.True(debtor.Probabilities.Sell > conservative.Probabilities.Sell);
    }

    [Fact]
    public void SelectedBuyAlwaysCreatesAnOrderEvenWhenConfiguredFanoutMinimumIsZero()
    {
        var options = new AutomatedTradingOptions
        {
            BuyOrdersPerCycleMin = 0,
            BuyOrdersPerCycleMax = 0,
        };
        var context = ContextFor(
            availableCash: 5_000m,
            sharesOwned: 0,
            companyPrice: 100m,
            priceChangePct: 0.10m);
        var assessment = EngineWith([0.99d], automatedTradingOptions: options).Evaluate(context);

        var intent = Assert.Single(EngineWith(
            [(double)(assessment.Probabilities.Buy / 2m), 0.50d],
            automatedTradingOptions: options).Decide(context));

        Assert.Equal(OrderType.Buy, intent.Type);
    }

    [Fact]
    public void PassiveOffsetsAreSymmetricAndStayInsideTheActiveBand()
    {
        var buyContext = ContextFor(
            availableCash: 5_000m,
            sharesOwned: 0,
            companyPrice: 100m,
            priceChangePct: 0.10m);
        var sellContext = ContextFor(
            availableCash: 0m,
            sharesOwned: 10,
            companyPrice: 100m,
            priceChangePct: -0.10m);
        var buyAssessment = EngineWith([0.99d]).Evaluate(buyContext);
        var sellAssessment = EngineWith([0.99d]).Evaluate(sellContext);

        var buyBelow = Assert.Single(EngineWith(
            [(double)(buyAssessment.Probabilities.Buy / 2m), 0d]).Decide(buyContext));
        var buyAbove = Assert.Single(EngineWith(
            [(double)(buyAssessment.Probabilities.Buy / 2m), 1d]).Decide(buyContext));
        var sellBelow = Assert.Single(EngineWith(
            [(double)(sellAssessment.Probabilities.Sell / 2m), 0d]).Decide(sellContext));
        var sellAbove = Assert.Single(EngineWith(
            [(double)(sellAssessment.Probabilities.Sell / 2m), 1d]).Decide(sellContext));

        Assert.Equal(100m - buyBelow.LimitPrice, buyAbove.LimitPrice - 100m);
        Assert.Equal(100m - sellBelow.LimitPrice, sellAbove.LimitPrice - 100m);
        Assert.All(
            new[] { buyBelow, buyAbove, sellBelow, sellAbove },
            intent => Assert.True(Bounds(100m).IsWithinActiveBand(intent.LimitPrice)));
    }

    [Fact]
    public void ExecutableAskAndBuyPoliciesRemainExecutionGates()
    {
        var executable = AutomatedContextFor(
            riskProfile: RiskProfile.Medium,
            netWorth: 10_000m,
            holdingsValue: 0m,
            availableBalance: 10_000m,
            buyingPower: 10_000m,
            bestAskPrice: 103m,
            bestAskQuantity: 5,
            issuedShares: 10_000,
            priceChangePct: 0.10m);
        var executableAssessment = EngineWith([0.99d]).Evaluate(executable);
        var executableBuy = Assert.Single(EngineWith(
            [(double)(executableAssessment.Probabilities.Buy / 2m)]).Decide(executable));
        var exposed = executable with { HoldingsValue = 6_000m };
        var crisis = executable with
        {
            CrisisActive = true,
            Participant = NewParticipant(
                10_000m,
                Temperament.Conservative,
                RiskProfile.Low),
        };
        var calm = crisis with { CrisisActive = false };
        var calmAssessment = EngineWith([0.99d]).Evaluate(calm);
        var crisisAssessment = EngineWith([0.99d]).Evaluate(crisis);
        var suppressed = EngineWith([
            (double)(crisisAssessment.Probabilities.Buy / 2m),
            1d,
        ]).Decide(crisis);

        Assert.Equal(103m, executableBuy.LimitPrice);
        Assert.Equal(0m, EngineWith([0.99d]).Evaluate(exposed).Probabilities.Buy);
        Assert.Equal(calmAssessment.Probabilities, crisisAssessment.Probabilities);
        Assert.Empty(suppressed);
    }

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
                new CompanyQuote(1, Price: 100m, PriceChangePct: 0.10m, OrderFlowImbalance: 1m, Bounds: Bounds(100m)),
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
    public void RiskProfileDoesNotAddPersonalityNoiseWithoutFinancialEvidence()
    {
        var high = CountOrders(Temperament.Balanced, RiskProfile.High);
        var medium = CountOrders(Temperament.Balanced, RiskProfile.Medium);
        var low = CountOrders(Temperament.Balanced, RiskProfile.Low);

        Assert.Equal(high, medium);
        Assert.Equal(medium, low);
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
    public void PositiveSectorSentimentRaisesDirectionalEvidence()
    {
        var engine = EngineWith([0.99d]);
        var positive = engine.Evaluate(ContextFor(
            availableCash: 1_000m,
            sharesOwned: 0,
            companyPrice: 100m,
            sectorSentiment: 1_000)).DirectionalScores[1];
        var neutral = engine.Evaluate(ContextFor(
            availableCash: 1_000m,
            sharesOwned: 0,
            companyPrice: 100m,
            sectorSentiment: 0)).DirectionalScores[1];
        var negative = engine.Evaluate(ContextFor(
            availableCash: 1_000m,
            sharesOwned: 0,
            companyPrice: 100m,
            sectorSentiment: -1_000)).DirectionalScores[1];

        Assert.True(positive > neutral);
        Assert.True(neutral > negative);
    }

    [Fact]
    public void NegativeSectorSentimentMirrorsPositiveDirectionalEvidence()
    {
        var engine = EngineWith([0.99d]);
        var negative = engine.Evaluate(ContextFor(
            availableCash: 0m,
            sharesOwned: 10,
            companyPrice: 100m,
            sectorSentiment: -1_000)).DirectionalScores[1];
        var positive = engine.Evaluate(ContextFor(
            availableCash: 0m,
            sharesOwned: 10,
            companyPrice: 100m,
            sectorSentiment: 1_000)).DirectionalScores[1];

        Assert.Equal(positive, -negative);
    }

    [Fact]
    public void SectorSentimentClampsAtThePlusAndMinusThousandEquivalents()
    {
        var engine = EngineWith([0.99d]);
        var positiveAtLimit = engine.Evaluate(ContextFor(
            1_000m, 0, 100m, sectorSentiment: 1_000)).DirectionalScores[1];
        var positiveBeyondLimit = engine.Evaluate(ContextFor(
            1_000m, 0, 100m, sectorSentiment: 5_000)).DirectionalScores[1];
        var negativeAtLimit = engine.Evaluate(ContextFor(
            0m, 10, 100m, sectorSentiment: -1_000)).DirectionalScores[1];
        var negativeBeyondLimit = engine.Evaluate(ContextFor(
            0m, 10, 100m, sectorSentiment: -5_000)).DirectionalScores[1];

        Assert.Equal(positiveAtLimit, positiveBeyondLimit);
        Assert.Equal(negativeAtLimit, negativeBeyondLimit);
    }

    [Fact]
    public void CombinedSignalsStayNormalized()
    {
        var engine = EngineWith([0.99d]);
        var buy = engine.Evaluate(ContextFor(
                availableCash: 1_000m,
                sharesOwned: 0,
                companyPrice: 100m,
                priceChangePct: 0.10m,
                orderFlowImbalance: 1m,
                longRangeChangePct: -0.80m,
                sectorSentiment: 1_000));
        var sell = engine.Evaluate(ContextFor(
                availableCash: 0m,
                sharesOwned: 10,
                companyPrice: 100m,
                priceChangePct: -0.10m,
                longRangeChangePct: 0.80m,
                sectorSentiment: -1_000));

        Assert.InRange(buy.DirectionalScores[1], -1m, 1m);
        Assert.InRange(sell.DirectionalScores[1], -1m, 1m);
        AssertDistribution(buy.Probabilities);
        AssertDistribution(sell.Probabilities);
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
        Assert.Equal(0, positiveRandom.IntegerDrawCount);
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

    private static void AssertDistribution(TradeActionProbabilities probabilities)
    {
        Assert.InRange(probabilities.Buy, 0m, 1m);
        Assert.InRange(probabilities.Sell, 0m, 1m);
        Assert.InRange(probabilities.Wait, 0m, 1m);
        Assert.Equal(1m, probabilities.Buy + probabilities.Sell + probabilities.Wait);
    }

    private static DecisionContext ContextWithQuote(
        CompanyQuote quote,
        decimal availableCash = 5_000m,
        int sharesOwned = 10,
        decimal loanLiability = 0m,
        Temperament temperament = Temperament.Balanced,
        RiskProfile riskProfile = RiskProfile.Medium)
    {
        var participant = NewParticipant(availableCash, temperament, riskProfile);
        return new DecisionContext(
            participant,
            availableCash,
            [quote],
            sharesOwned > 0
                ? new Dictionary<int, int> { [quote.CompanyId] = sharesOwned }
                : new Dictionary<int, int>(),
            new HashSet<int>(),
            LoanLiability: loanLiability);
    }

    private static EffectiveAuditEvidence Audit(CompanyRiskRating rating) => new(
        rating,
        TotalScore: 0,
        EvaluationStartTradingDayNumber: 1,
        EvaluationEndTradingDayNumber: 2,
        EffectiveTradingDayNumber: 3,
        AdjustedReturnScore: 0,
        CycleJumpScore: 0,
        FreeShareEmissionScore: 0,
        DenominationScore: 0,
        DividendOutcomeScore: 0,
        DividendCoverageScore: 0,
        IndustryScore: 0,
        ProfitabilityFactorScore: 0,
        StabilityFactorScore: 0,
        ClosureRiskFactorScore: 0,
        ManagementOutlookFactorScore: 0);

    private static LatestFinancialEvidence Financial(
        decimal profitability,
        decimal stability,
        decimal closureRisk,
        ManagementOutlook outlook,
        decimal coverage)
    {
        var current = new CompanyFinancialValues(
            Revenue: 1_000m,
            NetProfit: 100m,
            OperatingCashFlow: 120m,
            TotalAssets: 2_000m,
            TotalLiabilities: 700m,
            TotalDebt: 300m,
            ExpectedDividendPerShare: 2m,
            ExpectedDividendPool: 200m,
            DividendCoverageRatio: coverage,
            BusinessRiskScore: closureRisk,
            ManagementRevenueForecast: outlook switch
            {
                ManagementOutlook.Positive => 1_200m,
                ManagementOutlook.Negative => 800m,
                _ => 1_000m,
            },
            ManagementProfitForecast: outlook switch
            {
                ManagementOutlook.Positive => 130m,
                ManagementOutlook.Negative => 70m,
                _ => 100m,
            },
            ManagementOperatingCashFlowForecast: outlook switch
            {
                ManagementOutlook.Positive => 150m,
                ManagementOutlook.Negative => 90m,
                _ => 120m,
            });
        var deltas = new CompanyFinancialDeltas(
            Revenue: 0m,
            NetProfit: 0m,
            OperatingCashFlow: 0m,
            TotalAssets: 0m,
            TotalLiabilities: 0m,
            TotalDebt: 0m,
            ExpectedDividendPerShare: 0m,
            ExpectedDividendPool: 0m,
            DividendCoverageRatio: 0m,
            BusinessRiskScore: 0m,
            ManagementRevenueForecast: 0m,
            ManagementProfitForecast: 0m,
            ManagementOperatingCashFlowForecast: 0m,
            ManagementConfidenceScore: 0m);

        return new LatestFinancialEvidence(
            SnapshotId: 1,
            TradingDayNumber: 1,
            CompanyFinancialSnapshotMoment.Midday,
            current,
            deltas,
            ProfitabilityScore: profitability,
            ProfitabilityLevel: profitability >= 67m ? CompanyMetricLevel.High : profitability <= 33m
                ? CompanyMetricLevel.Low
                : CompanyMetricLevel.Medium,
            StabilityScore: stability,
            FinancialVolatilityLevel: stability >= 67m ? CompanyMetricLevel.Low : stability <= 33m
                ? CompanyMetricLevel.High
                : CompanyMetricLevel.Medium,
            ClosureRiskScore: closureRisk,
            ClosureRiskLevel: closureRisk >= 67m ? CompanyMetricLevel.High : closureRisk <= 33m
                ? CompanyMetricLevel.Low
                : CompanyMetricLevel.Medium,
            ManagementOutlook: outlook,
            ManagementConfidenceScore: 80m,
            LatestDividendOutcome: DividendFundingOutcome.Paid,
            LatestDividendDeclaredAmount: 200m,
            LatestDividendFundedAmount: 200m);
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
        decimal orderFlowImbalance = 0m,
        decimal longRangeChangePct = 0m,
        int sectorSentiment = 0,
        Temperament temperament = Temperament.Balanced,
        RiskProfile riskProfile = RiskProfile.Medium,
        OrderPriceBounds? bounds = null)
    {
        const int companyId = 1;

        var holdings = sharesOwned > 0
            ? new Dictionary<int, int> { [companyId] = sharesOwned }
            : new Dictionary<int, int>();

        return new DecisionContext(
            NewParticipant(availableCash, temperament, riskProfile),
            availableCash,
            [new CompanyQuote(companyId, companyPrice, priceChangePct, orderFlowImbalance, longRangeChangePct, sectorSentiment, bounds ?? Bounds(companyPrice))],
            holdings,
            new HashSet<int>(companiesWithOpenOrders ?? []));
    }

    private static DecisionContext AutomatedContextFor(
        RiskProfile riskProfile,
        decimal netWorth,
        decimal holdingsValue,
        decimal availableBalance,
        decimal buyingPower,
        decimal? bestAskPrice,
        int bestAskQuantity,
        int issuedShares,
        decimal priceChangePct = 0m,
        decimal longRangeChangePct = 0m,
        decimal marginLiability = 0m,
        decimal reservedBuyNotional = 0m,
        Temperament temperament = Temperament.Balanced,
        OrderPriceBounds? bounds = null,
        int? openSellQuantity = null)
    {
        const int companyId = 1;
        var price = 100m;
        var sharesOwned = holdingsValue > 0m ? (int)(holdingsValue / price) : 0;
        var participant = NewParticipant(
            availableBalance + reservedBuyNotional,
            temperament,
            riskProfile);
        participant.ReservedBalance = reservedBuyNotional;

        return new DecisionContext(
            participant,
            AvailableCash: buyingPower,
            [new CompanyQuote(
                companyId,
                price,
                PriceChangePct: priceChangePct,
                LongRangeChangePct: longRangeChangePct,
                Bounds: bounds ?? Bounds(price),
                IssuedShares: issuedShares,
                BestExecutableSellPrice: bestAskPrice,
                BestExecutableSellQuantity: bestAskQuantity,
                OpenSellQuantity: openSellQuantity ?? (bestAskPrice is null ? 0 : bestAskQuantity))],
            sharesOwned > 0
                ? new Dictionary<int, int> { [companyId] = sharesOwned }
                : new Dictionary<int, int>(),
            new HashSet<int>(),
            HoldingsValue: holdingsValue,
            NetWorth: netWorth,
            AvailableBalance: availableBalance,
            BuyingPower: buyingPower,
            MarginLiability: marginLiability,
            ReservedBuyNotional: reservedBuyNotional,
            HasAutomatedTradingData: true);
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

        public List<int> IntegerMaxValues { get; } = [];

        public int RemainingDoubleDraws => doubles.Count;

        public int RemainingIntegerDraws => ints.Count;

        public override double NextDouble()
        {
            DoubleDrawCount++;
            return doubles.Dequeue();
        }

        public override int Next(int maxValue)
        {
            IntegerDrawCount++;
            IntegerMaxValues.Add(maxValue);
            return ints.Dequeue();
        }
    }

    private sealed class OneTradeSizer : ITradeSizer
    {
        public int Size(Temperament temperament, int maxQuantity) => Math.Min(1, maxQuantity);
    }
}
