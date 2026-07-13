using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class TradingClockServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext context;
    private readonly TradingClockService clock;

    public TradingClockServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options);
        context.Database.EnsureCreated();
        clock = new TradingClockService(context, Options.Create(new TradingClockOptions
        {
            TradingCyclesPerDay = 210,
            TradingCycleSeconds = 2,
            BreakDurationSeconds = 60,
        }));
    }

    [Fact]
    public async Task CycleTwoHundredTenStartsOneBreakWithoutCreatingATradingCycle()
    {
        var seed = await SeedAsync(tradingCycleNumber: 210);

        var nextCycle = await clock.CompleteTradingCycleAsync(seed.Market, seed.Cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

        Assert.Null(nextCycle);
        Assert.Equal(TradingSessionState.Break, seed.Day.State);
        Assert.Equal(seed.Cycle.Id, seed.Day.ClosedInCycleId);
        var breakCycle = await context.TradingBreakCycles.SingleAsync();
        Assert.True(breakCycle.IsActive);
        Assert.Equal(60, breakCycle.DurationSeconds);
        Assert.Equal(0, breakCycle.ElapsedSeconds);
        Assert.Single(await context.MarketCycles.ToListAsync());
    }

    [Fact]
    public async Task ThirtyBreakTicksOpenTheNextDayAtTradingCycleOne()
    {
        var seed = await SeedAsync(tradingCycleNumber: 210);
        await clock.CompleteTradingCycleAsync(seed.Market, seed.Cycle, DateTime.UtcNow);
        await context.SaveChangesAsync();

        for (var tick = 1; tick <= 29; tick++)
        {
            var result = await clock.AdvanceBreakAsync(seed.Market, DateTime.UtcNow);
            Assert.False(result.OpenedNextDay);
            Assert.Equal(tick * 2, result.ElapsedSeconds);
            Assert.Single(await context.MarketCycles.ToListAsync());
        }

        var completed = await clock.AdvanceBreakAsync(seed.Market, DateTime.UtcNow);

        Assert.True(completed.OpenedNextDay);
        Assert.Equal(60, completed.ElapsedSeconds);
        var nextDay = await context.TradingDays.SingleAsync(day => day.DayNumber == 8);
        Assert.Equal(TradingSessionState.Trading, nextDay.State);
        var nextCycle = await context.MarketCycles.SingleAsync(cycle => cycle.TradingDayId == nextDay.Id);
        Assert.Equal(211, nextCycle.CycleNumber);
        Assert.Equal(1, nextCycle.TradingCycleNumber);
        Assert.Equal(nextCycle.Id, seed.Market.CurrentCycleId);
        Assert.Equal(nextDay.Id, seed.Market.CurrentTradingDayId);
        Assert.False((await context.TradingBreakCycles.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task SnapshotReportsSevenMinuteTradingPhaseAndOneMinuteBreak()
    {
        var seed = await SeedAsync(tradingCycleNumber: 84);

        var trading = await clock.GetStateAsync(seed.Market);

        Assert.Equal(7, trading!.TradingDayNumber);
        Assert.Equal(TradingSessionState.Trading, trading.TradingSessionState);
        Assert.Equal(84, trading.TradingCycleNumber);
        Assert.Equal(126, trading.RemainingTradingCycles);
        Assert.Equal(252, trading.RemainingPhaseSeconds);
        Assert.Equal("Advance one trading cycle", trading.NextStepMeaning);

        seed.Day.State = TradingSessionState.Break;
        seed.Cycle.TradingCycleNumber = 210;
        context.TradingBreakCycles.Add(new TradingBreakCycle
        {
            TradingDayId = seed.Day.Id,
            StartedAfterCycleId = seed.Cycle.Id,
            ElapsedSeconds = 16,
            DurationSeconds = 60,
            IsActive = true,
        });
        await context.SaveChangesAsync();

        var resting = await clock.GetStateAsync(seed.Market);

        Assert.Equal(TradingSessionState.Break, resting!.TradingSessionState);
        Assert.Equal(0, resting.RemainingTradingCycles);
        Assert.Equal(44, resting.RemainingPhaseSeconds);
        Assert.Equal("Advance the break countdown by 2 seconds", resting.NextStepMeaning);
    }

    private async Task<(Market Market, TradingDay Day, MarketCycle Cycle)> SeedAsync(int tradingCycleNumber)
    {
        var cycle = new MarketCycle
        {
            CycleNumber = tradingCycleNumber,
            TradingCycleNumber = tradingCycleNumber,
            Status = CycleStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        context.MarketCycles.Add(cycle);
        await context.SaveChangesAsync();

        var day = new TradingDay
        {
            DayNumber = 7,
            State = TradingSessionState.Trading,
            OpenedInCycleId = cycle.Id,
        };
        context.TradingDays.Add(day);
        await context.SaveChangesAsync();

        cycle.TradingDayId = day.Id;
        var market = new Market
        {
            Name = "Clock test",
            Status = MarketStatus.Running,
            CurrentCycleId = cycle.Id,
            CurrentTradingDayId = day.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Markets.Add(market);
        await context.SaveChangesAsync();
        return (market, day, cycle);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
