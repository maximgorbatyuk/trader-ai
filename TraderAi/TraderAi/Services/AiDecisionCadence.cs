namespace TraderAi.Services;

// Spreads an agent's allowed decisions across a trading day. The count comes from configuration; the positions are
// derived here so the schedule is deterministic and testable. The final entry is the end-of-day planning call whose
// orders are deferred to the next day's opening cycle.
public static class AiDecisionCadence
{
    // The planning call sits this many cycles before the close so a full order book still forms before the day ends.
    private const int PlanningBufferFromClose = 10;

    private const int FirstCycle = 2;

    // Ordered, distinct within-day cycle numbers at which the agent decides. Empty when the agent is disabled by a
    // non-positive count. The last element is always the end-of-day planning cycle.
    public static IReadOnlyList<int> DecisionCycles(int maxDecisionsPerDay, int tradingCyclesPerDay)
    {
        var n = Math.Clamp(maxDecisionsPerDay, 0, Math.Max(0, tradingCyclesPerDay));
        if (n <= 0)
        {
            return [];
        }

        var last = Math.Max(FirstCycle, tradingCyclesPerDay - PlanningBufferFromClose);
        if (n == 1)
        {
            return [last];
        }

        var first = Math.Min(FirstCycle, last);
        var cycles = new SortedSet<int>();
        for (var i = 0; i < n; i++)
        {
            var position = first + (int)Math.Round(i * (double)(last - first) / (n - 1));
            cycles.Add(Math.Clamp(position, 1, tradingCyclesPerDay));
        }

        return cycles.ToArray();
    }
}
