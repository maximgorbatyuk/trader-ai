using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Every thirtieth cycle, scores each active trader on how it has behaved over the last thirty cycles and uses
// those scores to reclassify the Player and the Player's Fund. Five activity metrics are read from the window's
// orders and trades, min-max normalised across the audited population, and summed into a TemperamentIndex (all
// five) and a RiskProfileIndex (the three that read as risk appetite). The fixed-personality traders' average
// index per Temperament and per RiskProfile then forms reference clusters, and the Player and Player's Fund are
// snapped to their nearest cluster on each axis. Draws no randomness and stages every change on the shared
// context, so the caller owns the save.
public sealed class BehaviorAuditService(AppDbContext dbContext)
{
    // The audit runs on cycles whose number is a multiple of this, looking back over this many cycles.
    private const int AuditCadenceCycles = 30;

    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        if (currentCycleNumber <= 0 || currentCycleNumber % AuditCadenceCycles != 0)
        {
            return;
        }

        var windowCycleIds = (await dbContext.MarketCycles
                .Where(cycle => cycle.CycleNumber > currentCycleNumber - AuditCadenceCycles
                    && cycle.CycleNumber <= currentCycleNumber)
                .Select(cycle => cycle.Id)
                .ToListAsync())
            .ToHashSet();

        // The audited population is every active trading actor; companies never trade and are excluded.
        var participants = await dbContext.Participants
            .Where(participant => participant.IsActive
                && (participant.Type == ParticipantType.Individual
                    || participant.Type == ParticipantType.AIAgent
                    || participant.Type == ParticipantType.CollectiveFund
                    || participant.Type == ParticipantType.Player))
            .ToListAsync();
        if (participants.Count == 0)
        {
            return;
        }

        var ordersByParticipant = (await dbContext.Orders
                .Where(order => order.ParticipantId != null && windowCycleIds.Contains(order.CreatedInCycleId))
                .Select(order => new { ParticipantId = order.ParticipantId!.Value, order.LimitPrice, order.Quantity })
                .ToListAsync())
            .GroupBy(order => order.ParticipantId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(order => new OrderMetric(order.LimitPrice, order.Quantity)).ToList());

        var priceDiffsByParticipant = await LoadPriceDiffsByParticipantAsync(windowCycleIds);

        var metrics = participants
            .Select(participant => MetricsFor(participant, ordersByParticipant, priceDiffsByParticipant))
            .ToList();

        NormaliseAndStoreIndices(participants, metrics);

        var playerFundParticipantId = await dbContext.CollectiveFunds
            .Where(fund => fund.IsPlayerManaged && fund.Status != CollectiveFundStatus.Closed)
            .Select(fund => (int?)fund.ParticipantId)
            .FirstOrDefaultAsync();

        ReclassifyPlayerAndFund(participants, playerFundParticipantId, currentCycleId, now);
    }

    private readonly record struct RawMetrics(
        double AvgOrdersPerCycle,
        double TotalOrders,
        double AvgOrderCostPerShare,
        double AvgSharesPerOrder,
        double AvgMarketPriceDiff);

    private static RawMetrics MetricsFor(
        Participant participant,
        IReadOnlyDictionary<int, List<OrderMetric>> ordersByParticipant,
        IReadOnlyDictionary<int, List<decimal>> priceDiffsByParticipant)
    {
        var orders = ordersByParticipant.GetValueOrDefault(participant.Id) ?? [];
        var totalOrders = orders.Count;
        var avgOrdersPerCycle = totalOrders / (double)AuditCadenceCycles;
        var avgOrderCostPerShare = totalOrders > 0 ? orders.Average(order => (double)order.LimitPrice) : 0.0;
        var avgSharesPerOrder = totalOrders > 0 ? orders.Average(order => order.Quantity) : 0.0;

        var diffs = priceDiffsByParticipant.GetValueOrDefault(participant.Id) ?? [];
        var avgMarketPriceDiff = diffs.Count > 0 ? diffs.Average(diff => (double)diff) : 0.0;

        return new RawMetrics(avgOrdersPerCycle, totalOrders, avgOrderCostPerShare, avgSharesPerOrder, avgMarketPriceDiff);
    }

    // Averages each trade's absolute move away from the market price just before it, per participant. The price a
    // trade executed away from is the snapshot immediately preceding the trade's own snapshot for that company,
    // and a trade counts for both its buyer and its seller.
    private async Task<Dictionary<int, List<decimal>>> LoadPriceDiffsByParticipantAsync(HashSet<int> windowCycleIds)
    {
        var snapshots = await dbContext.PriceSnapshots
            .Where(snapshot => windowCycleIds.Contains(snapshot.CreatedInCycleId))
            .OrderBy(snapshot => snapshot.Id)
            .Select(snapshot => new { snapshot.Id, snapshot.CompanyId, snapshot.Price, snapshot.SourceShareTransactionId })
            .ToListAsync();

        var beforePriceByTransactionId = new Dictionary<int, decimal>();
        foreach (var companySnapshots in snapshots.GroupBy(snapshot => snapshot.CompanyId))
        {
            var ordered = companySnapshots.OrderBy(snapshot => snapshot.Id).ToList();
            for (var index = 1; index < ordered.Count; index++)
            {
                if (ordered[index].SourceShareTransactionId is int transactionId)
                {
                    beforePriceByTransactionId[transactionId] = ordered[index - 1].Price;
                }
            }
        }

        var trades = await dbContext.ShareTransactions
            .Where(transaction => windowCycleIds.Contains(transaction.CreatedInCycleId))
            .Select(transaction => new { transaction.Id, transaction.SellerId, transaction.BuyerId, transaction.Price })
            .ToListAsync();

        var diffsByParticipant = new Dictionary<int, List<decimal>>();
        foreach (var trade in trades)
        {
            if (!beforePriceByTransactionId.TryGetValue(trade.Id, out var before) || before <= 0m)
            {
                continue;
            }

            var diff = Math.Abs(trade.Price - before) / before;
            AddDiff(diffsByParticipant, trade.BuyerId, diff);
            if (trade.SellerId is int sellerId)
            {
                AddDiff(diffsByParticipant, sellerId, diff);
            }
        }

        return diffsByParticipant;
    }

    private static void AddDiff(Dictionary<int, List<decimal>> diffsByParticipant, int participantId, decimal diff)
    {
        if (!diffsByParticipant.TryGetValue(participantId, out var list))
        {
            list = [];
            diffsByParticipant[participantId] = list;
        }

        list.Add(diff);
    }

    // TemperamentIndex sums all five normalised metrics; RiskProfileIndex sums the three that read as risk
    // appetite (trade frequency, order size, and how far trades push the market).
    private static void NormaliseAndStoreIndices(List<Participant> participants, List<RawMetrics> metrics)
    {
        var ordersPerCycle = metrics.Select(metric => metric.AvgOrdersPerCycle).ToList();
        var totalOrders = metrics.Select(metric => metric.TotalOrders).ToList();
        var costPerShare = metrics.Select(metric => metric.AvgOrderCostPerShare).ToList();
        var sharesPerOrder = metrics.Select(metric => metric.AvgSharesPerOrder).ToList();
        var priceDiff = metrics.Select(metric => metric.AvgMarketPriceDiff).ToList();

        var ordersPerCycleBounds = (ordersPerCycle.Min(), ordersPerCycle.Max());
        var totalOrdersBounds = (totalOrders.Min(), totalOrders.Max());
        var costPerShareBounds = (costPerShare.Min(), costPerShare.Max());
        var sharesPerOrderBounds = (sharesPerOrder.Min(), sharesPerOrder.Max());
        var priceDiffBounds = (priceDiff.Min(), priceDiff.Max());

        for (var index = 0; index < participants.Count; index++)
        {
            var metric = metrics[index];
            var n1 = Normalise(metric.AvgOrdersPerCycle, ordersPerCycleBounds);
            var n2 = Normalise(metric.TotalOrders, totalOrdersBounds);
            var n3 = Normalise(metric.AvgOrderCostPerShare, costPerShareBounds);
            var n4 = Normalise(metric.AvgSharesPerOrder, sharesPerOrderBounds);
            var n5 = Normalise(metric.AvgMarketPriceDiff, priceDiffBounds);

            participants[index].TemperamentIndex = (decimal)(n1 + n2 + n3 + n4 + n5);
            participants[index].RiskProfileIndex = (decimal)(n1 + n4 + n5);
        }
    }

    private static double Normalise(double value, (double Min, double Max) bounds) =>
        bounds.Max > bounds.Min ? (value - bounds.Min) / (bounds.Max - bounds.Min) : 0.0;

    // Reclassifies only the Player and the Player's Fund by snapping each to the nearest fixed-personality cluster
    // average on its axis. TODO: extend the reassignment to every participant once the design settles.
    private void ReclassifyPlayerAndFund(List<Participant> participants, int? playerFundParticipantId, int currentCycleId, DateTime now)
    {
        var player = participants.FirstOrDefault(participant => participant.Type == ParticipantType.Player);
        var playerFund = playerFundParticipantId is int fundParticipantId
            ? participants.FirstOrDefault(participant => participant.Id == fundParticipantId)
            : null;

        if (player is null && playerFund is null)
        {
            return;
        }

        var excludedIds = new HashSet<int>();
        if (player is not null)
        {
            excludedIds.Add(player.Id);
        }
        if (playerFund is not null)
        {
            excludedIds.Add(playerFund.Id);
        }

        var fixedPersonality = participants.Where(participant => !excludedIds.Contains(participant.Id)).ToList();
        var temperamentAverages = fixedPersonality
            .GroupBy(participant => participant.Temperament)
            .ToDictionary(group => group.Key, group => group.Average(participant => participant.TemperamentIndex));
        var riskAverages = fixedPersonality
            .GroupBy(participant => participant.RiskProfile)
            .ToDictionary(group => group.Key, group => group.Average(participant => participant.RiskProfileIndex));

        if (temperamentAverages.Count == 0 || riskAverages.Count == 0)
        {
            return;
        }

        ApplyNearest(player, temperamentAverages, riskAverages);
        ApplyNearest(playerFund, temperamentAverages, riskAverages);

        PostAuditNews(player, playerFund, currentCycleId, now);
    }

    private static void ApplyNearest(
        Participant? subject,
        IReadOnlyDictionary<Temperament, decimal> temperamentAverages,
        IReadOnlyDictionary<RiskProfile, decimal> riskAverages)
    {
        if (subject is null)
        {
            return;
        }

        subject.Temperament = Nearest(subject.TemperamentIndex, temperamentAverages);
        subject.RiskProfile = Nearest(subject.RiskProfileIndex, riskAverages);
    }

    // Picks the group whose average index is closest to the subject's, breaking ties toward the earliest enum
    // value so the classification is deterministic.
    private static TGroup Nearest<TGroup>(decimal index, IReadOnlyDictionary<TGroup, decimal> averages)
        where TGroup : struct, Enum
    {
        TGroup best = default;
        var hasBest = false;
        var bestDistance = 0m;
        foreach (var candidate in Enum.GetValues<TGroup>())
        {
            if (!averages.TryGetValue(candidate, out var average))
            {
                continue;
            }

            var distance = Math.Abs(index - average);
            if (!hasBest || distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
                hasBest = true;
            }
        }

        return best;
    }

    private void PostAuditNews(Participant? player, Participant? playerFund, int currentCycleId, DateTime now)
    {
        var parts = new List<string>();
        if (player is not null)
        {
            parts.Add($"{player.Name} now reads as {player.Temperament} / {player.RiskProfile}");
        }
        if (playerFund is not null)
        {
            parts.Add($"{playerFund.Name} now reads as {playerFund.Temperament} / {playerFund.RiskProfile}");
        }

        if (parts.Count == 0)
        {
            return;
        }

        dbContext.NewsPosts.Add(new NewsPost
        {
            Title = "Behavioural audit updates trading profiles",
            Content = $"A review of the last {AuditCadenceCycles} cycles of trading reclassified the player desk: {string.Join("; ", parts)}.",
            PublishedInCycleId = currentCycleId,
            PublishedAt = now,
            Scope = NewsImpactScope.None,
            Category = NewsCategory.General,
        });
    }

    private readonly record struct OrderMetric(decimal LimitPrice, int Quantity);
}
