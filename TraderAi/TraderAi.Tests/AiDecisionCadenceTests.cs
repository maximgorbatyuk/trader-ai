using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AiDecisionCadenceTests
{
    private const int CyclesPerDay = 210;

    [Fact]
    public void NonPositiveCountProducesNoCycles()
    {
        Assert.Empty(AiDecisionCadence.DecisionCycles(0, CyclesPerDay));
        Assert.Empty(AiDecisionCadence.DecisionCycles(-3, CyclesPerDay));
    }

    [Fact]
    public void OneDecisionIsTheEndOfDayPlanningCycle()
    {
        Assert.Equal(new[] { 200 }, AiDecisionCadence.DecisionCycles(1, CyclesPerDay));
    }

    [Fact]
    public void ThreeDecisionsSpanBeginningMiddleAndEnd()
    {
        Assert.Equal(new[] { 2, 101, 200 }, AiDecisionCadence.DecisionCycles(3, CyclesPerDay));
    }

    [Fact]
    public void FiveDecisionsAreEvenlySpreadBetweenTheAnchors()
    {
        Assert.Equal(new[] { 2, 52, 101, 150, 200 }, AiDecisionCadence.DecisionCycles(5, CyclesPerDay));
    }

    [Fact]
    public void CyclesAreDistinctAndStrictlyIncreasingWithAnchoredEndpoints()
    {
        var cycles = AiDecisionCadence.DecisionCycles(7, CyclesPerDay);

        Assert.Equal(7, cycles.Count);
        Assert.Equal(2, cycles[0]);
        Assert.Equal(200, cycles[^1]);
        for (var i = 1; i < cycles.Count; i++)
        {
            Assert.True(cycles[i] > cycles[i - 1]);
        }
    }

    [Fact]
    public void CountAboveDistinctPositionsCollapsesButStaysWithinTheDay()
    {
        var cycles = AiDecisionCadence.DecisionCycles(1000, CyclesPerDay);

        Assert.Equal(2, cycles[0]);
        Assert.Equal(200, cycles[^1]);
        Assert.Equal(cycles.Distinct().Count(), cycles.Count);
        Assert.All(cycles, cycle => Assert.InRange(cycle, 1, CyclesPerDay));
    }

    [Fact]
    public void PositionsScaleToADifferentDayLength()
    {
        var cycles = AiDecisionCadence.DecisionCycles(3, 20);

        Assert.Equal(3, cycles.Count);
        Assert.Equal(2, cycles[0]);
        Assert.Equal(10, cycles[^1]);
    }
}
