using TraderAi.Models;

namespace TraderAi.Tests;

public sealed class PriceBandStateTests
{
    private static PriceBandState Band() => new()
    {
        CompanyId = 1,
        State = LuldState.Normal,
        ReferencePrice = 100m,
        LowerBandPrice = 85m,
        UpperBandPrice = 110m,
    };

    [Fact]
    public void ClampPullsAPriceBelowTheBandUpToTheLowerEdge()
    {
        Assert.Equal(85m, Band().ClampToActiveBand(80m));
    }

    [Fact]
    public void ClampPullsAnAnomalousPriceAboveTheBandDownToTheUpperEdge()
    {
        Assert.Equal(110m, Band().ClampToActiveBand(140m));
    }

    [Fact]
    public void ClampLeavesAPriceInsideTheBandUntouched()
    {
        Assert.Equal(97m, Band().ClampToActiveBand(97m));
    }

    [Fact]
    public void ClampIsANoOpWithoutAReferencePrice()
    {
        var band = new PriceBandState { CompanyId = 1, ReferencePrice = 0m, LowerBandPrice = 0m, UpperBandPrice = 0m };

        Assert.Equal(42m, band.ClampToActiveBand(42m));
    }
}
