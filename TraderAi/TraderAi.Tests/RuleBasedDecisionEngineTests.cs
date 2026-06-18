using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class RuleBasedDecisionEngineTests
{
    private readonly RuleBasedDecisionEngine engine = new();

    [Fact]
    public void HolderSellsRiskFractionOfHoldingsBelowMarketWhenAggressive()
    {
        var context = ContextFor(
            Temperament.Aggressive,
            RiskProfile.High,
            availableCash: 0m,
            sharesOwned: 10,
            companyPrice: 100m);

        var intent = Assert.Single(engine.Decide(context));

        Assert.Equal(OrderType.Sell, intent.Type);
        Assert.Equal(5, intent.Quantity);
        Assert.Equal(90m, intent.LimitPrice);
    }

    [Fact]
    public void CashHolderBuysRiskFractionOfBudgetAboveMarketWhenAggressive()
    {
        var context = ContextFor(
            Temperament.Aggressive,
            RiskProfile.High,
            availableCash: 5000m,
            sharesOwned: 0,
            companyPrice: 100m);

        var intent = Assert.Single(engine.Decide(context));

        Assert.Equal(OrderType.Buy, intent.Type);
        Assert.Equal(110m, intent.LimitPrice);
        Assert.Equal(22, intent.Quantity);
    }

    [Fact]
    public void ConservativeHolderAsksAboveMarketAndCommitsLittle()
    {
        var context = ContextFor(
            Temperament.Conservative,
            RiskProfile.Low,
            availableCash: 0m,
            sharesOwned: 10,
            companyPrice: 100m);

        var intent = Assert.Single(engine.Decide(context));

        Assert.Equal(OrderType.Sell, intent.Type);
        Assert.Equal(1, intent.Quantity);
        Assert.Equal(110m, intent.LimitPrice);
    }

    [Fact]
    public void NoIntentWhenCompanyAlreadyHasAnOpenOrder()
    {
        var context = ContextFor(
            Temperament.Aggressive,
            RiskProfile.High,
            availableCash: 5000m,
            sharesOwned: 10,
            companyPrice: 100m,
            companiesWithOpenOrders: [1]);

        Assert.Empty(engine.Decide(context));
    }

    [Fact]
    public void NoIntentWhenBudgetCannotAffordASingleShare()
    {
        var context = ContextFor(
            Temperament.Aggressive,
            RiskProfile.High,
            availableCash: 50m,
            sharesOwned: 0,
            companyPrice: 100m);

        Assert.Empty(engine.Decide(context));
    }

    [Fact]
    public void NoIntentWhenHoldingsAreTooSmallForRiskFraction()
    {
        var context = ContextFor(
            Temperament.Balanced,
            RiskProfile.Low,
            availableCash: 0m,
            sharesOwned: 5,
            companyPrice: 100m);

        Assert.Empty(engine.Decide(context));
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
