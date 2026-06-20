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
        var context = ContextFor(Temperament.Aggressive, RiskProfile.High, availableCash: 0m, sharesOwned: 10, companyPrice: 100m);

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
        var context = ContextFor(Temperament.Aggressive, RiskProfile.High, availableCash: 5000m, sharesOwned: 0, companyPrice: 100m);

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
            Temperament.Aggressive,
            RiskProfile.High,
            availableCash: 5000m,
            sharesOwned: 10,
            companyPrice: 100m,
            companiesWithOpenOrders: [1]);

        Assert.Empty(CollectIntents(context));
    }

    [Fact]
    public void DoesNothingWhenItHasNoCashAndNoShares()
    {
        var context = ContextFor(Temperament.Balanced, RiskProfile.Low, availableCash: 0m, sharesOwned: 0, companyPrice: 100m);

        Assert.Empty(CollectIntents(context));
    }

    [Fact]
    public void BuyLimitCrossesAboveMarketForAnAggressiveTrader()
    {
        var context = ContextFor(Temperament.Aggressive, RiskProfile.High, availableCash: 5000m, sharesOwned: 0, companyPrice: 100m);

        Assert.All(CollectIntents(context), intent => Assert.Equal(110m, intent.LimitPrice));
    }

    [Fact]
    public void SellLimitCrossesBelowMarketForAnAggressiveTrader()
    {
        var context = ContextFor(Temperament.Aggressive, RiskProfile.High, availableCash: 0m, sharesOwned: 10, companyPrice: 100m);

        Assert.All(CollectIntents(context), intent => Assert.Equal(90m, intent.LimitPrice));
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
        Temperament temperament,
        RiskProfile riskProfile,
        decimal availableCash,
        int sharesOwned,
        decimal companyPrice,
        int[]? companiesWithOpenOrders = null)
    {
        const int companyId = 1;

        var participant = new Participant
        {
            Name = "Trader",
            Type = ParticipantType.Individual,
            Temperament = temperament,
            RiskProfile = riskProfile,
            InitialBalance = availableCash,
            CurrentBalance = availableCash,
            IsActive = true,
        };

        var holdings = sharesOwned > 0
            ? new Dictionary<int, int> { [companyId] = sharesOwned }
            : new Dictionary<int, int>();

        return new DecisionContext(
            participant,
            availableCash,
            [new CompanyQuote(companyId, companyPrice)],
            holdings,
            new HashSet<int>(companiesWithOpenOrders ?? []));
    }
}
