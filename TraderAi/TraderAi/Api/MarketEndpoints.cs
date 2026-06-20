using Microsoft.EntityFrameworkCore;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Api;

public static class MarketEndpoints
{
    public static void MapMarketEndpoints(this WebApplication app)
    {
        app.MapGet("/companies", async (AppDbContext dbContext) =>
        {
            var companies = await dbContext.Companies.OrderBy(company => company.Id).ToListAsync();
            var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);

            var response = companies
                .Select(company => new CompanyResponse(
                    company.Id,
                    company.Name,
                    company.IssuedSharesCount,
                    latestPriceByCompany.GetValueOrDefault(company.Id)))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/market", async (MarketService marketService) =>
        {
            var market = await marketService.GetMarketAsync();
            return Results.Ok(market is null ? null : ToMarketResponse(market));
        });

        app.MapPost("/market/seed", async (MarketService marketService) =>
        {
            if (await marketService.GetMarketAsync() is not null)
            {
                return Results.Conflict(new { error = "A market already exists." });
            }

            var market = await marketService.SeedDemoMarketAsync();
            return Results.Ok(ToMarketResponse(market));
        });

        app.MapPost("/market/pause", async (MarketService marketService) =>
        {
            var market = await marketService.SetStatusAsync(MarketStatus.Paused);
            return market is null
                ? Results.NotFound(new { error = "No market exists." })
                : Results.Ok(ToMarketResponse(market));
        });

        app.MapPost("/market/start", async (MarketService marketService) =>
        {
            var market = await marketService.SetStatusAsync(MarketStatus.Running);
            return market is null
                ? Results.NotFound(new { error = "No market exists." })
                : Results.Ok(ToMarketResponse(market));
        });

        app.MapGet("/participants", async (AppDbContext dbContext) =>
        {
            var participants = await dbContext.Participants.OrderBy(participant => participant.Id).ToListAsync();

            var sharesByOwner = await dbContext.Shares
                .Where(share => share.OwnerId != null)
                .GroupBy(share => share.OwnerId!.Value)
                .Select(group => new { OwnerId = group.Key, Count = group.Count() })
                .ToListAsync();

            var sharesOwnedByParticipant = sharesByOwner.ToDictionary(entry => entry.OwnerId, entry => entry.Count);

            var response = participants
                .Select(participant => new ParticipantResponse(
                    participant.Id,
                    participant.Name,
                    participant.Type.ToString(),
                    participant.Temperament.ToString(),
                    participant.RiskProfile.ToString(),
                    participant.CurrentBalance,
                    participant.ReservedBalance,
                    participant.AvailableBalance,
                    sharesOwnedByParticipant.GetValueOrDefault(participant.Id),
                    participant.IsActive))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/participants/{participantId:int}/holdings", async (int participantId, AppDbContext dbContext) =>
        {
            var sharesByCompany = await dbContext.Shares
                .Where(share => share.OwnerId == participantId)
                .GroupBy(share => share.CompanyId)
                .Select(group => new { CompanyId = group.Key, Shares = group.Count() })
                .ToListAsync();

            var companyNameById = await dbContext.Companies
                .ToDictionaryAsync(company => company.Id, company => company.Name);

            var response = sharesByCompany
                .OrderByDescending(holding => holding.Shares)
                .Select(holding => new HoldingResponse(
                    holding.CompanyId,
                    companyNameById.GetValueOrDefault(holding.CompanyId, $"#{holding.CompanyId}"),
                    holding.Shares))
                .ToArray();

            return Results.Ok(response);
        });

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

        app.MapPost("/cycles/tick", async (MarketService marketService) =>
        {
            var result = await marketService.StepCycleAsync();
            return Results.Ok(new CycleTickResponse(
                result.Ran,
                result.CompletedCycleNumber,
                result.OrdersPlaced,
                result.FillCount));
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

            var cycles = await dbContext.MarketCycles.OrderBy(cycle => cycle.CycleNumber).ToListAsync();
            var response = cycles
                .Select(cycle => new ActivityPointResponse(
                    cycle.CycleNumber,
                    ordersPlacedByCycleId.GetValueOrDefault(cycle.Id)))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/transactions/shares", async (int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 50, 1, 500);
            var transactions = await dbContext.ShareTransactions
                .OrderByDescending(transaction => transaction.Id)
                .Take(limit)
                .ToListAsync();

            var response = transactions
                .Select(transaction => new ShareTransactionResponse(
                    transaction.Id,
                    transaction.SellerId,
                    transaction.BuyerId,
                    transaction.CompanyId,
                    transaction.Quantity,
                    transaction.Price,
                    transaction.TotalCost,
                    transaction.CreatedInCycleId,
                    transaction.CreatedAt))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/prices/{companyId:int}", async (int companyId, AppDbContext dbContext) =>
        {
            var snapshots = await dbContext.PriceSnapshots
                .Where(snapshot => snapshot.CompanyId == companyId)
                .OrderBy(snapshot => snapshot.Id)
                .ToListAsync();

            var response = snapshots
                .Select(snapshot => new PriceSnapshotResponse(
                    snapshot.Id,
                    snapshot.CompanyId,
                    snapshot.Price,
                    snapshot.CreatedInCycleId,
                    snapshot.CreatedAt))
                .ToArray();

            return Results.Ok(response);
        });
    }

    // The current company price is the most recent snapshot, found per company by its max Id so the
    // whole snapshot history never has to be loaded.
    private static async Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync(AppDbContext dbContext)
    {
        var latestSnapshotIds = await dbContext.PriceSnapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .Select(group => group.Max(snapshot => snapshot.Id))
            .ToListAsync();

        return (await dbContext.PriceSnapshots
                .Where(snapshot => latestSnapshotIds.Contains(snapshot.Id))
                .Select(snapshot => new { snapshot.CompanyId, snapshot.Price })
                .ToListAsync())
            .ToDictionary(row => row.CompanyId, row => row.Price);
    }

    private static MarketResponse ToMarketResponse(Market market) =>
        new(market.Id, market.Name, market.Status.ToString(), market.CurrentCycleId);

    private static OrderResponse ToOrderResponse(Order order) => new(
        order.Id,
        order.ParticipantId,
        order.CompanyId,
        order.Type.ToString(),
        order.Status.ToString(),
        order.Quantity,
        order.FilledQuantity,
        order.LimitPrice,
        order.ReservedCashAmount,
        order.CreatedInCycleId);
}

public sealed record CompanyResponse(int Id, string Name, int IssuedSharesCount, decimal? CurrentPrice);

public sealed record MarketResponse(int Id, string Name, string Status, int? CurrentCycleId);

public sealed record ParticipantResponse(
    int Id,
    string Name,
    string Type,
    string Temperament,
    string RiskProfile,
    decimal CurrentBalance,
    decimal ReservedBalance,
    decimal AvailableBalance,
    int SharesOwned,
    bool IsActive);

public sealed record PlaceOrderRequest(int ParticipantId, int CompanyId, OrderType Type, int Quantity, decimal LimitPrice);

public sealed record OrderResponse(
    int Id,
    int? ParticipantId,
    int CompanyId,
    string Type,
    string Status,
    int Quantity,
    int FilledQuantity,
    decimal LimitPrice,
    decimal ReservedCashAmount,
    int CreatedInCycleId);

public sealed record CycleTickResponse(bool Ran, int? CompletedCycleNumber, int OrdersPlaced, int FillCount);

public sealed record ActivityPointResponse(int CycleNumber, int OrdersPlaced);

public sealed record HoldingResponse(int CompanyId, string CompanyName, int Shares);

public sealed record CycleResponse(int Id, int CycleNumber, string Status, DateTime? StartedAt, DateTime? CompletedAt);

public sealed record ShareTransactionResponse(
    int Id,
    int? SellerId,
    int BuyerId,
    int CompanyId,
    int Quantity,
    decimal Price,
    decimal TotalCost,
    int CreatedInCycleId,
    DateTime CreatedAt);

public sealed record PriceSnapshotResponse(int Id, int CompanyId, decimal Price, int CreatedInCycleId, DateTime CreatedAt);
