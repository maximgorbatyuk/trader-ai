using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Tests;

public sealed class MarketRunPersistenceTests
{
    [Fact]
    public async Task PersistsRunIdentityAcrossMarketCycleAndAiCall()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var context = new AppDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            var run = new MarketRun { StartedAt = DateTime.UtcNow };
            context.MarketRuns.Add(run);
            await context.SaveChangesAsync();

            var cycle = new MarketCycle
            {
                CycleNumber = 1,
                MarketRunId = run.Id,
                Status = CycleStatus.Running,
                StartedAt = DateTime.UtcNow
            };
            context.MarketCycles.Add(cycle);
            await context.SaveChangesAsync();

            context.Markets.Add(new Market
            {
                Name = "Market",
                Status = MarketStatus.Running,
                CurrentRunId = run.Id,
                CurrentCycleId = cycle.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            context.AiTraderCalls.Add(new AiTraderCall
            {
                MarketRunId = run.Id,
                ParticipantId = 1,
                ParticipantName = "Provider-backed trader",
                ProviderId = "provider",
                ProviderLabel = "Provider",
                Model = "model",
                SnapshotCycleId = cycle.Id,
                SnapshotCycleNumber = cycle.CycleNumber,
                PromptHash = "hash",
                RequestJson = "{}",
                Status = AiTraderCallStatus.Pending,
                RequestedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        await using var verification = new AppDbContext(options);
        var persistedRun = await verification.MarketRuns.SingleAsync();
        var persistedMarket = await verification.Markets.SingleAsync();
        var persistedCycle = await verification.MarketCycles.SingleAsync();
        var persistedCall = await verification.AiTraderCalls.SingleAsync();

        Assert.Equal(persistedRun.Id, persistedMarket.CurrentRunId);
        Assert.Equal(persistedRun.Id, persistedCycle.MarketRunId);
        Assert.Equal(persistedRun.Id, persistedCall.MarketRunId);
    }
}
