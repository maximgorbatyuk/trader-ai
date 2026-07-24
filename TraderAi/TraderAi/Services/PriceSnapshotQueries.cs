using TraderAi.Data;
using Microsoft.EntityFrameworkCore;

namespace TraderAi.Services;

// Shared read helper for the latest price per company. It forwards to the context's per-tick cache so the many
// per-cycle callers share one query per generation, and the read stays untracked to keep the change tracker clean.
internal static class PriceSnapshotQueries
{
    public static Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync(AppDbContext dbContext) =>
        dbContext.LatestPriceByCompanyAsync();

    public static async Task<Dictionary<int, decimal>> LatestPriceByCompanyAtOrBeforeCycleAsync(
        AppDbContext dbContext,
        int marketRunId,
        int maximumCycleNumber)
    {
        var eligibleSnapshots =
            from snapshot in dbContext.PriceSnapshots.AsNoTracking()
            join cycle in dbContext.MarketCycles.AsNoTracking()
                on snapshot.CreatedInCycleId equals cycle.Id
            where cycle.MarketRunId == marketRunId
                && cycle.CycleNumber <= maximumCycleNumber
            select snapshot;
        var latestSnapshotIds = await eligibleSnapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .Select(group => group.Max(snapshot => snapshot.Id))
            .ToListAsync();

        return await dbContext.PriceSnapshots.AsNoTracking()
            .Where(snapshot => latestSnapshotIds.Contains(snapshot.Id))
            .ToDictionaryAsync(snapshot => snapshot.CompanyId, snapshot => snapshot.Price);
    }

    public static async Task<decimal?> LatestPriceAtOrBeforeCycleAsync(
        AppDbContext dbContext,
        int marketRunId,
        int companyId,
        int targetCycleNumber)
    {
        var live =
            from snapshot in dbContext.PriceSnapshots
            join cycle in dbContext.MarketCycles on snapshot.CreatedInCycleId equals cycle.Id
            where cycle.MarketRunId == marketRunId
                && snapshot.CompanyId == companyId
                && cycle.CycleNumber <= targetCycleNumber
            select new { snapshot.Id, cycle.CycleNumber, snapshot.Price };

        var archived =
            from snapshot in dbContext.PriceSnapshotArchives
            join cycle in dbContext.MarketCycles on snapshot.CreatedInCycleId equals cycle.Id
            where snapshot.MarketRunId == marketRunId
                && snapshot.CompanyId == companyId
                && cycle.CycleNumber <= targetCycleNumber
            select new { snapshot.Id, cycle.CycleNumber, snapshot.Price };

        return await live
            .Concat(archived)
            .OrderByDescending(point => point.CycleNumber)
            .ThenByDescending(point => point.Id)
            .Select(point => (decimal?)point.Price)
            .FirstOrDefaultAsync();
    }
}
