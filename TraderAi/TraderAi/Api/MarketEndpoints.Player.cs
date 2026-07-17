using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Api;

public static partial class MarketEndpoints
{
    public static void MapPlayerEndpoints(this WebApplication app)
    {
        app.MapGet("/player", async (AppDbContext dbContext, MarginService marginService) =>
            Results.Ok(await BuildPlayerResponseAsync(dbContext, marginService)));

        app.MapPut("/player/favorite-companies/{companyId:int}", async (int companyId, AppDbContext dbContext) =>
            await SetFavoriteCompanyAsync(dbContext, companyId, true));

        app.MapDelete("/player/favorite-companies/{companyId:int}", async (int companyId, AppDbContext dbContext) =>
            await SetFavoriteCompanyAsync(dbContext, companyId, false));

        app.MapPost("/player", async (CreatePlayerRequest? request, MarketService marketService, AppDbContext dbContext, MarginService marginService) =>
        {
            var result = await marketService.CreatePlayerAsync(request?.Name);
            if (!result.Success)
            {
                // The absent market is a bad request; an existing player is a conflict.
                return result.Error == "No market exists."
                    ? Results.BadRequest(new { error = result.Error })
                    : Results.Conflict(new { error = result.Error });
            }

            return Results.Ok(await BuildPlayerResponseAsync(dbContext, marginService));
        });

        app.MapPost("/player/orders/{orderId:int}/cancel", async (int orderId, MarketService marketService) =>
        {
            var result = await marketService.CancelPlayerOrderAsync(orderId);
            return result is { Success: true, Order: not null }
                ? Results.Ok(ToOrderResponse(result.Order))
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapPost("/player/fund", async (OpenPlayerFundRequest request, MarketService marketService, AppDbContext dbContext, MarginService marginService) =>
        {
            var result = await marketService.OpenPlayerFundAsync(request.SeedAmount, request.Name);
            if (!result.Success)
            {
                // An existing managed fund is a conflict; everything else is a bad request.
                return result.Error == "The player already manages a fund."
                    ? Results.Conflict(new { error = result.Error })
                    : Results.BadRequest(new { error = result.Error });
            }

            return Results.Ok(await BuildPlayerResponseAsync(dbContext, marginService));
        });

        app.MapPost("/player/fund/deposit", async (PlayerFundCashRequest request, MarketService marketService, AppDbContext dbContext, MarginService marginService) =>
        {
            var result = await marketService.DepositToPlayerFundAsync(request.Amount);
            return result.Success
                ? Results.Ok(await BuildPlayerResponseAsync(dbContext, marginService))
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapPost("/player/fund/withdraw", async (PlayerFundCashRequest request, MarketService marketService, AppDbContext dbContext, MarginService marginService) =>
        {
            var result = await marketService.WithdrawFromPlayerFundAsync(request.Amount);
            return result.Success
                ? Results.Ok(await BuildPlayerResponseAsync(dbContext, marginService))
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapPost("/player/fund/close", async (MarketService marketService, AppDbContext dbContext, MarginService marginService) =>
        {
            var result = await marketService.ClosePlayerFundAsync();
            return result.Success
                ? Results.Ok(await BuildPlayerResponseAsync(dbContext, marginService))
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapGet("/funds/{id:int}/advertise-quote", async (int id, MarketService marketService) =>
        {
            var result = await marketService.GetFundAdvertiseQuoteAsync(id);
            return result is { Success: true, Quote: not null }
                ? Results.Ok(new FundAdvertiseQuoteResponse(
                    result.Quote.Price,
                    result.Quote.Fraction,
                    result.Quote.GrowthPct,
                    result.Quote.FundWorth,
                    result.Quote.PopularityIndex))
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapPost("/funds/{id:int}/advertise", async (int id, MarketService marketService, AppDbContext dbContext, MarginService marginService) =>
        {
            var result = await marketService.AdvertiseFundAsync(id);
            return result.Success
                ? Results.Ok(await BuildPlayerResponseAsync(dbContext, marginService))
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapGet("/collective-funds/closed", async (int? page, int? pageSize, AppDbContext dbContext) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 20);

            var query = dbContext.CollectiveFunds.Where(fund => fund.Status == CollectiveFundStatus.Closed);
            var total = await query.CountAsync();

            var funds = await query
                .OrderByDescending(fund => fund.ClosedAt)
                .ThenByDescending(fund => fund.Id)
                .Skip((pageIndex - 1) * size)
                .Take(size)
                .ToListAsync();

            // The fund's Participant row is kept (never deleted) on close, so its name and personality still resolve.
            var participantIds = funds.Select(fund => fund.ParticipantId).ToList();
            var participantById = await dbContext.Participants
                .Where(participant => participantIds.Contains(participant.Id))
                .ToDictionaryAsync(participant => participant.Id);
            var cycleNumberById = await dbContext.MarketCycles
                .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

            var items = funds
                .Select(fund =>
                {
                    participantById.TryGetValue(fund.ParticipantId, out var participant);
                    return new ClosedFundResponse(
                        fund.Id,
                        fund.ParticipantId,
                        participant?.Name ?? $"#{fund.ParticipantId}",
                        participant?.Temperament.ToString(),
                        participant?.RiskProfile.ToString(),
                        fund.PeakNetWorth,
                        cycleNumberById.GetValueOrDefault(fund.CreatedInCycleId),
                        fund.ClosedAt);
                })
                .ToArray();

            return Results.Ok(new PagedClosedFundsResponse(items, total, pageIndex, size));
        });
    }
}
