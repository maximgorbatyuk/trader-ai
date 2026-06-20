using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

// Reproduces the original deterministic rule (one intent per company, size from a risk fraction) so the
// orchestration tests can assert exact order counts and trade prices without the randomness of the
// production engine.
internal sealed class DeterministicDecisionEngine : IDecisionEngine
{
    public IReadOnlyList<OrderIntent> Decide(DecisionContext context)
    {
        var intents = new List<OrderIntent>();
        var riskFraction = RiskFraction(context.Participant.RiskProfile);

        foreach (var quote in context.Companies)
        {
            if (context.CompaniesWithOpenOrders.Contains(quote.CompanyId))
            {
                continue;
            }

            var sharesOwned = context.SharesOwnedByCompany.GetValueOrDefault(quote.CompanyId);

            if (sharesOwned > 0)
            {
                var quantity = (int)Math.Floor(sharesOwned * riskFraction);
                if (quantity < 1)
                {
                    continue;
                }

                var sellLimit = Round(quote.Price * SellMultiplier(context.Participant.Temperament));
                if (sellLimit > 0)
                {
                    intents.Add(new OrderIntent(OrderType.Sell, quote.CompanyId, quantity, sellLimit));
                }

                continue;
            }

            var buyLimit = Round(quote.Price * BuyMultiplier(context.Participant.Temperament));
            if (buyLimit <= 0)
            {
                continue;
            }

            var buyQuantity = (int)Math.Floor(context.AvailableCash * riskFraction / buyLimit);
            if (buyQuantity >= 1)
            {
                intents.Add(new OrderIntent(OrderType.Buy, quote.CompanyId, buyQuantity, buyLimit));
            }
        }

        return intents;
    }

    private static decimal RiskFraction(RiskProfile riskProfile) => riskProfile switch
    {
        RiskProfile.High => 0.50m,
        RiskProfile.Medium => 0.25m,
        RiskProfile.Low => 0.10m,
        _ => 0.10m,
    };

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

// Never trades, so a manual tick advances and settles only the orders a test placed by hand.
internal sealed class NoOpDecisionEngine : IDecisionEngine
{
    public IReadOnlyList<OrderIntent> Decide(DecisionContext context) => [];
}

// Always returns the full cap, so engine tests can check the upper bound the engine passes to the sizer.
internal sealed class MaxTradeSizer : ITradeSizer
{
    public int Size(Temperament temperament, int maxQuantity) => maxQuantity;
}
