using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class RuleBasedDecisionEngineTests
{
    // MaxTradeSizer returns the full cap, so quantity assertions probe the upper bound the engine sets.
    private readonly RuleBasedDecisionEngine engine = new(new MaxTradeSizer(), new Random(20260619));

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

    [Fact]
    public void BuyLimitSitsOneToFivePercentAboveMarket()
    {
        var context = ContextFor(availableCash: 5000m, sharesOwned: 0, companyPrice: 100m);

        Assert.All(CollectIntents(context), intent => Assert.InRange(intent.LimitPrice, 101m, 105m));
    }

    [Fact]
    public void SellLimitSitsOneToFivePercentBelowMarket()
    {
        var context = ContextFor(availableCash: 0m, sharesOwned: 10, companyPrice: 100m);

        Assert.All(CollectIntents(context), intent => Assert.InRange(intent.LimitPrice, 95m, 99m));
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
                new CompanyQuote(1, Price: 100m, PriceChangePct: 0.10m, NetShareDemand: 1000),
                new CompanyQuote(2, Price: 100m),
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
        var withDebt = CountSells(DebtorContext(currentBalance: -180m, sharesOwned: 10, companyPrice: 100m, RiskProfile.Low));
        var noDebt = CountSells(DebtorContext(currentBalance: 0m, sharesOwned: 10, companyPrice: 100m, RiskProfile.Low));

        Assert.True(withDebt > noDebt, $"debt {withDebt} should raise selling above no-debt {noDebt}");
    }

    [Fact]
    public void LowRiskDeleveragesHarderThanHighRisk()
    {
        // Isolate the debt-driven pull from the baseline personality by comparing each profile's own increase.
        var lowIncrease = CountSells(DebtorContext(-180m, 10, 100m, RiskProfile.Low))
            - CountSells(DebtorContext(0m, 10, 100m, RiskProfile.Low));
        var highIncrease = CountSells(DebtorContext(-180m, 10, 100m, RiskProfile.High))
            - CountSells(DebtorContext(0m, 10, 100m, RiskProfile.High));

        Assert.True(lowIncrease > highIncrease,
            $"low-risk debt selling increase {lowIncrease} should exceed high-risk {highIncrease}");
    }

    [Fact]
    public void DeeperDebtSellsMoreThanShallowDebt()
    {
        var deep = CountSells(DebtorContext(-180m, 10, 100m, RiskProfile.Medium));
        var shallow = CountSells(DebtorContext(-5m, 10, 100m, RiskProfile.Medium));

        Assert.True(deep > shallow, $"deep debt {deep} should sell more than shallow debt {shallow}");
    }

    // Same fixed seed per engine so the only difference between compared runs is the debt/risk under test.
    private static int CountSells(DecisionContext context)
    {
        var engine = new RuleBasedDecisionEngine(new MaxTradeSizer(), new Random(20260619));
        var sells = 0;
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            sells += engine.Decide(context).Count(intent => intent.Type == OrderType.Sell);
        }

        return sells;
    }

    // A trader carrying debt (negative balance) that still owns sellable shares, with no spare buying power.
    private static DecisionContext DebtorContext(decimal currentBalance, int sharesOwned, decimal companyPrice, RiskProfile riskProfile)
    {
        const int companyId = 1;
        var participant = new Participant
        {
            Name = "Debtor",
            Type = ParticipantType.Individual,
            Temperament = Temperament.Balanced,
            RiskProfile = riskProfile,
            InitialBalance = 0m,
            CurrentBalance = currentBalance,
            IsActive = true,
        };

        return new DecisionContext(
            participant,
            AvailableCash: 0m,
            [new CompanyQuote(companyId, companyPrice)],
            new Dictionary<int, int> { [companyId] = sharesOwned },
            new HashSet<int>());
    }

    // A flat, no-signal market with both cash and shares leaves every choice to the weighted fallback, so
    // the order count isolates how personality shifts trading frequency. A fresh, equally seeded engine per
    // profile keeps the only difference the profile itself.
    private static int CountOrders(Temperament temperament, RiskProfile riskProfile)
    {
        var engine = new RuleBasedDecisionEngine(new MaxTradeSizer(), new Random(20260619));
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
            [new CompanyQuote(companyId, companyPrice, priceChangePct, netShareDemand, longRangeChangePct)],
            holdings,
            new HashSet<int>(companiesWithOpenOrders ?? []));
    }

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
}
