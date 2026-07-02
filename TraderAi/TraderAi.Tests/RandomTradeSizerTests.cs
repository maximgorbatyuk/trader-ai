using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class RandomTradeSizerTests
{
    private const int Cap = 100;
    private const int Draws = 5000;

    [Fact]
    public void AggressiveAveragesLargerPackagesThanBalancedThanConservative()
    {
        var aggressive = AverageSize(Temperament.Aggressive);
        var balanced = AverageSize(Temperament.Balanced);
        var conservative = AverageSize(Temperament.Conservative);

        Assert.True(aggressive > balanced, $"aggressive {aggressive} should exceed balanced {balanced}");
        Assert.True(balanced > conservative, $"balanced {balanced} should exceed conservative {conservative}");
    }

    [Fact]
    public void StaysWithinOneAndTheCapForEveryTemperament()
    {
        var sizer = new RandomTradeSizer(new Random(20260702));

        foreach (var temperament in Enum.GetValues<Temperament>())
        {
            for (var draw = 0; draw < Draws; draw++)
            {
                Assert.InRange(sizer.Size(temperament, Cap), 1, Cap);
            }
        }
    }

    [Fact]
    public void ReturnsZeroWhenNothingIsAffordableOrOwned()
    {
        var sizer = new RandomTradeSizer(new Random(20260702));

        Assert.Equal(0, sizer.Size(Temperament.Aggressive, 0));
    }

    // Equal seeding makes the underlying uniform draws identical, so the averages differ only by the
    // temperament scaling.
    private static double AverageSize(Temperament temperament)
    {
        var sizer = new RandomTradeSizer(new Random(20260702));
        long total = 0;

        for (var draw = 0; draw < Draws; draw++)
        {
            total += sizer.Size(temperament, Cap);
        }

        return (double)total / Draws;
    }
}
