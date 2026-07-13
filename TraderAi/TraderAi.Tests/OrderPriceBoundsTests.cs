using TraderAi.Models;

namespace TraderAi.Tests;

public sealed class OrderPriceBoundsTests
{
    // The approved $100 worked example: active band -15%/+10%, allowed order range -25%/+15%.
    private static OrderPriceBounds HundredDollarBounds() =>
        OrderPriceBounds.FromReference(
            referencePrice: 100m,
            lowerBandPercent: 15m,
            upperBandPercent: 10m,
            allowedLowerPercent: 25m,
            allowedUpperPercent: 15m);

    [Fact]
    public void DerivesTheApprovedBandAndAllowedRangeForAHundredDollarReference()
    {
        var bounds = HundredDollarBounds();

        Assert.Equal(100m, bounds.ReferencePrice);
        Assert.Equal(85m, bounds.ActiveLowerPrice);
        Assert.Equal(110m, bounds.ActiveUpperPrice);
        Assert.Equal(75m, bounds.AllowedMinimumPrice);
        Assert.Equal(115m, bounds.AllowedMaximumPrice);
    }

    [Theory]
    [InlineData(85)]
    [InlineData(100)]
    [InlineData(110)]
    public void TreatsActiveBandBoundariesAsInclusive(decimal price)
    {
        Assert.True(HundredDollarBounds().IsWithinActiveBand(price));
    }

    [Theory]
    [InlineData(84.99)]
    [InlineData(110.01)]
    public void TreatsPricesJustOutsideTheActiveBandAsOutsideIt(decimal price)
    {
        Assert.False(HundredDollarBounds().IsWithinActiveBand(price));
    }

    [Theory]
    [InlineData(75)]
    [InlineData(80)]
    [InlineData(115)]
    public void TreatsAllowedRangeBoundariesAsInclusive(decimal price)
    {
        Assert.True(HundredDollarBounds().IsWithinAllowedRange(price));
    }

    [Theory]
    [InlineData(74.99)]
    [InlineData(115.01)]
    public void RejectsPricesOneCentBeyondTheAllowedRange(decimal price)
    {
        Assert.False(HundredDollarBounds().IsWithinAllowedRange(price));
    }

    [Fact]
    public void ClampsAPriceBelowTheBandUpToTheLowerBand()
    {
        Assert.Equal(85m, HundredDollarBounds().ClampToActiveBand(60m));
    }

    [Fact]
    public void ClampsAPriceAboveTheBandDownToTheUpperBand()
    {
        Assert.Equal(110m, HundredDollarBounds().ClampToActiveBand(180m));
    }

    [Fact]
    public void LeavesAPriceAlreadyInsideTheBandUntouched()
    {
        Assert.Equal(97m, HundredDollarBounds().ClampToActiveBand(97m));
    }

    [Fact]
    public void RoundsDerivedPricesToCentsAwayFromZero()
    {
        // 33.33 * 0.85 = 28.3305 -> 28.33; 33.33 * 0.75 = 24.9975 -> 25.00 (away from zero).
        var bounds = OrderPriceBounds.FromReference(33.33m, 15m, 10m, 25m, 15m);

        Assert.Equal(28.33m, bounds.ActiveLowerPrice);
        Assert.Equal(25.00m, bounds.AllowedMinimumPrice);
    }
}
