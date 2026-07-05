using Microsoft.EntityFrameworkCore;
using TraderAi.Data;

namespace TraderAi.Services;

// Shared read helpers for price snapshots. The latest price per company is found by its max Id so the whole
// snapshot history is never materialised, and the read is untracked so the many per-cycle callers cannot
// bloat the change tracker.
internal static class PriceSnapshotQueries
{
    public static async Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync(AppDbContext dbContext)
    {
        var latestSnapshotIds = await dbContext.PriceSnapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .Select(group => group.Max(snapshot => snapshot.Id))
            .ToListAsync();

        return (await dbContext.PriceSnapshots
                .AsNoTracking()
                .Where(snapshot => latestSnapshotIds.Contains(snapshot.Id))
                .Select(snapshot => new { snapshot.CompanyId, snapshot.Price })
                .ToListAsync())
            .ToDictionary(row => row.CompanyId, row => row.Price);
    }
}
