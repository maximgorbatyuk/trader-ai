using TraderAi.Data;

namespace TraderAi.Services;

// Shared read helper for the latest price per company. It forwards to the context's per-tick cache so the many
// per-cycle callers share one query per generation, and the read stays untracked to keep the change tracker clean.
internal static class PriceSnapshotQueries
{
    public static Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync(AppDbContext dbContext) =>
        dbContext.LatestPriceByCompanyAsync();
}
