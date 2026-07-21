using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class ParticipantDailyWorthSnapshotTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;

    public ParticipantDailyWorthSnapshotTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task DayCloseWritesOneDailyWorthRowPerParticipantMatchingTheCycleClose()
    {
        var (day, cycle) = await SeedTradingDayAtCycleAsync(tradingCycleNumber: 210);
        var service = BuildService();

        var advance = await service.AdvanceCycleAsync();

        Assert.True(advance.Success);
        var cycleSnapshots = await context.ParticipantWorthSnapshots
            .Where(snapshot => snapshot.CreatedInCycleId == cycle.Id)
            .ToListAsync();
        var dailySnapshots = await context.ParticipantDailyWorthSnapshots.ToListAsync();

        // The day closed, so each participant's per-cycle close is copied verbatim into the daily table.
        Assert.Equal(cycleSnapshots.Count, dailySnapshots.Count);
        Assert.NotEmpty(dailySnapshots);
        Assert.All(dailySnapshots, daily => Assert.Equal(day.Id, daily.TradingDayId));
        foreach (var cycleSnapshot in cycleSnapshots)
        {
            var daily = Assert.Single(dailySnapshots, candidate => candidate.ParticipantId == cycleSnapshot.ParticipantId);
            Assert.Equal(cycleSnapshot.Balance, daily.Balance);
            Assert.Equal(cycleSnapshot.HoldingsValue, daily.HoldingsValue);
            Assert.Equal(cycleSnapshot.LoanLiability, daily.LoanLiability);
            Assert.Equal(cycleSnapshot.MarginLiability, daily.MarginLiability);
        }
    }

    [Fact]
    public async Task NonClosingCycleWritesNoDailyWorthRows()
    {
        await SeedTradingDayAtCycleAsync(tradingCycleNumber: 5);
        var service = BuildService();

        var advance = await service.AdvanceCycleAsync();

        Assert.True(advance.Success);
        // The day did not close (cycle 5 of 210), so the per-cycle snapshot is written but no daily row is.
        Assert.NotEmpty(await context.ParticipantWorthSnapshots.ToListAsync());
        Assert.Empty(await context.ParticipantDailyWorthSnapshots.ToListAsync());
    }

    private MarketService BuildService() => new(
        context,
        new MatchingEngine(context),
        new NoOpDecisionEngine(),
        new MarketCycleLock(),
        new Random(1),
        tradingClockService: new TradingClockService(context, Options.Create(new TradingClockOptions())));

    // Seeds the classic two-participant market, then attaches its cycle to an open trading day at the given
    // per-day position so an advance either closes the day (position == cycles-per-day) or not.
    private async Task<(TradingDay Day, MarketCycle Cycle)> SeedTradingDayAtCycleAsync(int tradingCycleNumber)
    {
        await TestMarketSeed.SeedClassicScenarioAsync(context);
        var market = await context.Markets.SingleAsync();
        var cycle = await context.MarketCycles.SingleAsync();
        cycle.CycleNumber = tradingCycleNumber;
        cycle.TradingCycleNumber = tradingCycleNumber;
        // Keep the dividend window closed so the single advance stays deterministic and isolated from payouts.
        market.NextDividendCycleNumber = tradingCycleNumber + 1_000;

        var day = new TradingDay
        {
            DayNumber = 1,
            State = TradingSessionState.Trading,
            OpenedInCycleId = cycle.Id,
        };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();

        cycle.TradingDayId = day.Id;
        market.CurrentTradingDayId = day.Id;
        await context.SaveChangesAsync();
        return (day, cycle);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
