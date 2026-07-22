using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Api;

public static partial class MarketEndpoints
{
    public static void MapTradingEndpoints(this WebApplication app)
    {
        app.MapGet("/orders", async (string? status, AppDbContext dbContext) =>
        {
            var query = dbContext.Orders.AsQueryable();

            if (string.Equals(status, "open", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(order =>
                    order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled);
            }

            var orders = await query.OrderBy(order => order.Id).ToListAsync();
            return Results.Ok(orders.Select(ToOrderResponse).ToArray());
        });

        app.MapPost("/orders", async (PlaceOrderRequest request, MarketService marketService) =>
        {
            var result = await marketService.PlaceOrderAsync(
                request.ParticipantId,
                request.CompanyId,
                request.Type,
                request.Quantity,
                request.LimitPrice);

            return result is { Success: true, Order: not null }
                ? Results.Ok(ToOrderResponse(result.Order))
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapGet("/transactions/shares", async (int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 50, 1, 500);
            var transactions = await dbContext.ShareTransactions
                .Include(transaction => transaction.SettlementInstruction)
                .OrderByDescending(transaction => transaction.Id)
                .Take(limit)
                .ToListAsync();

            return Results.Ok(transactions.Select(ToShareTransactionResponse).ToArray());
        });

        app.MapGet("/investments", async (int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 50, 1, 500);
            var currentRunId = await dbContext.Markets.Select(market => market.CurrentRunId).SingleOrDefaultAsync();
            var investments = await dbContext.CompanyInvestments
                .Where(investment => investment.MarketRunId == currentRunId || investment.MarketRunId == null)
                .OrderByDescending(investment => investment.Id)
                .Take(limit)
                .ToListAsync();

            return Results.Ok(await ToInvestmentResponsesAsync(dbContext, investments));
        });

        app.MapGet("/transactions/shares/paged", async (int? page, int? pageSize, AppDbContext dbContext) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 20);
            var total = await dbContext.ShareTransactions.CountAsync();
            var transactions = await dbContext.ShareTransactions
                .Include(transaction => transaction.SettlementInstruction)
                .OrderByDescending(transaction => transaction.Id)
                .Skip((pageIndex - 1) * size)
                .Take(size)
                .ToListAsync();

            return Results.Ok(new PagedShareTransactionsResponse(
                transactions.Select(ToShareTransactionResponse).ToArray(),
                total,
                pageIndex,
                size));
        });
    }
}
