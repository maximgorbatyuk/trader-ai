using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Api;

public static partial class MarketEndpoints
{
    public static void MapMarketControlEndpoints(this WebApplication app)
    {
        app.MapGet("/market", async (
            MarketService marketService,
            TradingClockService tradingClockService,
            AppDbContext dbContext) =>
        {
            var market = await marketService.GetMarketAsync();
            if (market is null)
            {
                return Results.Ok<MarketResponse?>(null);
            }

            return Results.Ok<MarketResponse?>(await BuildMarketResponseAsync(market, dbContext, tradingClockService));
        });

        app.MapPost("/market/seed", async (
            MarketService marketService,
            TradingClockService tradingClockService,
            AppDbContext dbContext) =>
        {
            if (await marketService.GetMarketAsync() is not null)
            {
                return Results.Conflict(new { error = "A market already exists." });
            }

            var market = await marketService.SeedDemoMarketAsync();
            return Results.Ok(await BuildMarketResponseAsync(market, dbContext, tradingClockService));
        });

        app.MapPost("/market/reset", async (
            MarketService marketService,
            TradingClockService tradingClockService,
            AppDbContext dbContext) =>
        {
            var market = await marketService.ResetDemoMarketAsync();
            return Results.Ok(await BuildMarketResponseAsync(market, dbContext, tradingClockService));
        });

        app.MapPost("/market/pause", async (
            MarketService marketService,
            TradingClockService tradingClockService,
            AppDbContext dbContext) =>
        {
            var market = await marketService.SetStatusAsync(MarketStatus.Paused);
            return market is null
                ? Results.NotFound(new { error = "No market exists." })
                : Results.Ok(await BuildMarketResponseAsync(market, dbContext, tradingClockService));
        });

        app.MapPost("/market/start", async (
            MarketService marketService,
            TradingClockService tradingClockService,
            AppDbContext dbContext) =>
        {
            var market = await marketService.SetStatusAsync(MarketStatus.Running);
            return market is null
                ? Results.NotFound(new { error = "No market exists." })
                : Results.Ok(await BuildMarketResponseAsync(market, dbContext, tradingClockService));
        });

        app.MapGet("/cycles", async (AppDbContext dbContext) =>
        {
            var cycles = await dbContext.MarketCycles.OrderBy(cycle => cycle.CycleNumber).ToListAsync();

            var response = cycles
                .Select(cycle => new CycleResponse(
                    cycle.Id,
                    cycle.CycleNumber,
                    cycle.Status.ToString(),
                    cycle.StartedAt,
                    cycle.CompletedAt))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/cycles/activity", async (AppDbContext dbContext) =>
        {
            // Issuer seed sell orders all land in the first cycle, so only participant orders are
            // counted to keep the activity chart about ongoing trading rather than the initial listing.
            var ordersByCycle = await dbContext.Orders
                .Where(order => order.ParticipantId != null)
                .GroupBy(order => order.CreatedInCycleId)
                .Select(group => new { CycleId = group.Key, Count = group.Count() })
                .ToListAsync();

            var ordersPlacedByCycleId = ordersByCycle.ToDictionary(entry => entry.CycleId, entry => entry.Count);

            var dividendCycleIds = (await dbContext.MoneyTransactions
                    .Where(transaction => transaction.Type == MoneyTransactionType.Dividend)
                    .Select(transaction => transaction.CreatedInCycleId)
                    .Distinct()
                    .ToListAsync())
                .ToHashSet();

            var cycles = await (
                    from cycle in dbContext.MarketCycles
                    join day in dbContext.TradingDays on cycle.TradingDayId equals day.Id
                    orderby cycle.CycleNumber
                    select new
                    {
                        cycle.Id,
                        cycle.CycleNumber,
                        TradingDayNumber = day.DayNumber,
                        cycle.TradingCycleNumber,
                    })
                .ToListAsync();
            var response = cycles
                .Select(cycle => new ActivityPointResponse(
                    cycle.CycleNumber,
                    cycle.TradingDayNumber,
                    cycle.TradingCycleNumber,
                    ordersPlacedByCycleId.GetValueOrDefault(cycle.Id),
                    dividendCycleIds.Contains(cycle.Id)))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/prices/{companyId:int}", async (int companyId, AppDbContext dbContext) =>
        {
            var snapshots = await dbContext.PriceSnapshots
                .Where(snapshot => snapshot.CompanyId == companyId)
                .OrderBy(snapshot => snapshot.Id)
                .ToListAsync();

            var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);
            var response = snapshots
                .Select(snapshot => new PriceSnapshotResponse(
                    snapshot.Id,
                    snapshot.CompanyId,
                    snapshot.Price,
                    snapshot.Capitalization,
                    snapshot.CreatedInCycleId,
                    cycleNumbersById.GetValueOrDefault(snapshot.CreatedInCycleId),
                    snapshot.CreatedAt))
                .ToArray();

            return Results.Ok(response);
        });
    }
}
