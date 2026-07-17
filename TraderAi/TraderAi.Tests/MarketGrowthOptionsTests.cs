using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class MarketGrowthOptionsTests
{
    [Fact]
    public void DefaultsUseTheGrowthRebalanceValues()
    {
        var volatility = new VolatilityHaltOptions();
        var tradeFee = new TradeFeeOptions();
        var random = new RandomChanceRatesOptions();

        Assert.Equal(15m, volatility.UpperBandPercent);
        Assert.Equal(25m, volatility.AllowedOrderUpperPercent);
        Assert.Equal(0.25m, volatility.DemandRatchetStepPercent);
        Assert.Equal(0.005m, tradeFee.FeeRate);
        Assert.Equal(0.0003m, random.RandomMagnitudeBands.DividendRateMin);
        Assert.Equal(0.015m, random.RandomMagnitudeBands.DividendRateMax);
        Assert.Equal(0.02, random.RandomMagnitudeBands.PrimaryIssuanceRateMin);
        Assert.Equal(0.20, random.RandomMagnitudeBands.PrimaryIssuanceRateMax);
        Assert.Equal(0.08, random.EventTriggerChances.AuditorRaiseExpectationsChance);
        Assert.Equal(0.50, random.EventTriggerChances.BigInvestmentMax);
    }
}
