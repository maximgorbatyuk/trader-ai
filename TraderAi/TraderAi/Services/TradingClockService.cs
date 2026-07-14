using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record TradingClockState(
    int TradingDayNumber,
    TradingSessionState TradingSessionState,
    int TradingCycleNumber,
    int RemainingTradingCycles,
    int RemainingPhaseSeconds,
    int TradingCycleSeconds);

public sealed record BreakAdvanceResult(bool OpenedNextDay, int ElapsedSeconds, MarketCycle? OpenedCycle);

public sealed class TradingClockService(AppDbContext dbContext, IOptions<TradingClockOptions> options)
{
    private readonly TradingClockOptions settings = options.Value;

    public int TradingCyclesForSeconds(int seconds) =>
        Math.Max(1, (int)Math.Ceiling(seconds / (decimal)settings.TradingCycleSeconds));

    public async Task<bool> IsTradingAsync(Market market)
    {
        if (market.CurrentTradingDayId is not int dayId)
        {
            return true;
        }

        return await dbContext.TradingDays
            .Where(day => day.Id == dayId)
            .Select(day => day.State == TradingSessionState.Trading)
            .FirstOrDefaultAsync();
    }

    public async Task<TradingClockState?> GetStateAsync(Market market)
    {
        if (market.CurrentTradingDayId is not int dayId || market.CurrentCycleId is not int cycleId)
        {
            return null;
        }

        var day = await dbContext.TradingDays.FirstOrDefaultAsync(candidate => candidate.Id == dayId);
        var cycle = await dbContext.MarketCycles.FirstOrDefaultAsync(candidate => candidate.Id == cycleId);
        if (day is null || cycle is null)
        {
            return null;
        }

        var remainingCycles = day.State == TradingSessionState.Trading
            ? Math.Max(0, settings.TradingCyclesPerDay - cycle.TradingCycleNumber)
            : 0;
        var remainingSeconds = remainingCycles * settings.TradingCycleSeconds;

        if (day.State == TradingSessionState.Break)
        {
            var activeBreak = await dbContext.TradingBreakCycles
                .FirstOrDefaultAsync(candidate => candidate.TradingDayId == day.Id && candidate.IsActive);
            remainingSeconds = activeBreak is null
                ? 0
                : Math.Max(0, activeBreak.DurationSeconds - activeBreak.ElapsedSeconds);
        }

        return new TradingClockState(
            day.DayNumber,
            day.State,
            cycle.TradingCycleNumber,
            remainingCycles,
            remainingSeconds,
            settings.TradingCycleSeconds);
    }

    public async Task<MarketCycle?> CompleteTradingCycleAsync(Market market, MarketCycle currentCycle, DateTime now)
    {
        var day = await dbContext.TradingDays.SingleAsync(candidate => candidate.Id == currentCycle.TradingDayId);
        if (currentCycle.TradingCycleNumber < settings.TradingCyclesPerDay)
        {
            var nextCycle = new MarketCycle
            {
                CycleNumber = currentCycle.CycleNumber + 1,
                TradingDayId = day.Id,
                TradingCycleNumber = currentCycle.TradingCycleNumber + 1,
                Status = CycleStatus.Running,
                StartedAt = now,
            };
            dbContext.MarketCycles.Add(nextCycle);
            await dbContext.SaveChangesAsync();
            market.CurrentCycleId = nextCycle.Id;
            return nextCycle;
        }

        day.State = TradingSessionState.Break;
        day.ClosedInCycleId = currentCycle.Id;
        dbContext.TradingBreakCycles.Add(new TradingBreakCycle
        {
            TradingDayId = day.Id,
            StartedAfterCycleId = currentCycle.Id,
            ElapsedSeconds = 0,
            DurationSeconds = settings.BreakDurationSeconds,
            IsActive = true,
        });
        return null;
    }

    public async Task<BreakAdvanceResult> AdvanceBreakAsync(Market market, DateTime now)
    {
        if (market.CurrentTradingDayId is not int dayId)
        {
            return new BreakAdvanceResult(false, 0, null);
        }

        var activeBreak = await dbContext.TradingBreakCycles
            .SingleAsync(candidate => candidate.TradingDayId == dayId && candidate.IsActive);
        activeBreak.ElapsedSeconds = Math.Min(
            activeBreak.DurationSeconds,
            activeBreak.ElapsedSeconds + settings.TradingCycleSeconds);

        if (activeBreak.ElapsedSeconds < activeBreak.DurationSeconds)
        {
            await dbContext.SaveChangesAsync();
            return new BreakAdvanceResult(false, activeBreak.ElapsedSeconds, null);
        }

        activeBreak.IsActive = false;
        var currentCycleNumber = await dbContext.MarketCycles.MaxAsync(cycle => cycle.CycleNumber);
        var nextDayNumber = await dbContext.TradingDays.MaxAsync(day => day.DayNumber) + 1;
        var nextDay = new TradingDay
        {
            DayNumber = nextDayNumber,
            State = TradingSessionState.Trading,
            OpenedInCycleId = 0,
        };
        dbContext.TradingDays.Add(nextDay);
        await dbContext.SaveChangesAsync();

        var nextCycle = new MarketCycle
        {
            CycleNumber = currentCycleNumber + 1,
            TradingDayId = nextDay.Id,
            TradingCycleNumber = 1,
            Status = CycleStatus.Running,
            StartedAt = now,
        };
        dbContext.MarketCycles.Add(nextCycle);
        await dbContext.SaveChangesAsync();

        nextDay.OpenedInCycleId = nextCycle.Id;
        market.CurrentTradingDayId = nextDay.Id;
        market.CurrentCycleId = nextCycle.Id;
        market.UpdatedAt = now;
        await dbContext.SaveChangesAsync();
        return new BreakAdvanceResult(true, activeBreak.ElapsedSeconds, nextCycle);
    }
}
