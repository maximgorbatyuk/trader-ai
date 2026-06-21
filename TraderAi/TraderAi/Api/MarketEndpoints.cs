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
            var changeByCompany = await PriceChangePctByCompanyAsync(dbContext);
            var industryNameById = await IndustryNameByIdAsync(dbContext);

            var response = companies
                .Select(company => new CompanyResponse(
                    company.Id,
                    company.Name,
                    company.IndustryId,
                    industryNameById.GetValueOrDefault(company.IndustryId),
                    company.IssuedSharesCount,
                    latestPriceByCompany.GetValueOrDefault(company.Id),
                    changeByCompany.GetValueOrDefault(company.Id)))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/companies/{companyId:int}", async (int companyId, AppDbContext dbContext) =>
        {
            var detail = await BuildCompanyDetailAsync(dbContext, companyId);
            return detail is null
                ? Results.NotFound(new { error = "Company not found." })
                : Results.Ok(detail);
        });

        app.MapGet("/companies/{companyId:int}/shareholders", async (int companyId, AppDbContext dbContext) =>
        {
            // CurrentPrice on a held share is what its owner last paid, so summing per owner is the cost basis.
            var sharesByOwner = await dbContext.Shares
                .Where(share => share.CompanyId == companyId && share.OwnerId != null)
                .GroupBy(share => share.OwnerId!.Value)
                .Select(group => new { OwnerId = group.Key, Shares = group.Count(), CostBasis = group.Sum(share => share.CurrentPrice) })
                .ToListAsync();

            var ownerNameById = await dbContext.Participants
                .ToDictionaryAsync(participant => participant.Id, participant => participant.Name);
            var issuedShares = await dbContext.Companies
                .Where(company => company.Id == companyId)
                .Select(company => company.IssuedSharesCount)
                .FirstOrDefaultAsync();
            var currentPrice = (await LatestPriceByCompanyAsync(dbContext)).GetValueOrDefault(companyId);

            var response = sharesByOwner
                .OrderByDescending(holder => holder.Shares)
                .Select(holder => new ShareholderResponse(
                    holder.OwnerId,
                    ownerNameById.GetValueOrDefault(holder.OwnerId, $"#{holder.OwnerId}"),
                    holder.Shares,
                    currentPrice * holder.Shares,
                    holder.CostBasis,
                    issuedShares > 0 ? (decimal)holder.Shares / issuedShares : 0m))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/companies/{companyId:int}/orders", async (int companyId, int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 10, 1, 100);
            var orders = await dbContext.Orders
                .Where(order => order.CompanyId == companyId)
                .OrderByDescending(order => order.Id)
                .Take(limit)
                .ToListAsync();

            return Results.Ok(orders.Select(ToOrderResponse).ToArray());
        });

        app.MapGet("/companies/{companyId:int}/share-transactions", async (int companyId, int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 10, 1, 100);
            var transactions = await dbContext.ShareTransactions
                .Where(transaction => transaction.CompanyId == companyId)
                .OrderByDescending(transaction => transaction.Id)
                .Take(limit)
                .ToListAsync();

            return Results.Ok(transactions.Select(ToShareTransactionResponse).ToArray());
        });

        app.MapGet("/market", async (MarketService marketService, AppDbContext dbContext) =>
        {
            var market = await marketService.GetMarketAsync();
            if (market is null)
            {
                return Results.Ok<MarketResponse?>(null);
            }

            var lastDividendTotal = await LastDividendTotalAsync(dbContext);
            var currentCycleNumber = market.CurrentCycleId is int cycleId
                ? await dbContext.MarketCycles
                    .Where(cycle => cycle.Id == cycleId)
                    .Select(cycle => (int?)cycle.CycleNumber)
                    .FirstOrDefaultAsync()
                : null;
            return Results.Ok<MarketResponse?>(ToMarketResponse(market, lastDividendTotal, currentCycleNumber));
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

        app.MapPost("/market/reset", async (MarketService marketService) =>
        {
            var market = await marketService.ResetDemoMarketAsync();
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

            var holdingsByOwner = (await dbContext.Shares
                    .Where(share => share.OwnerId != null)
                    .GroupBy(share => new { OwnerId = share.OwnerId!.Value, share.CompanyId })
                    .Select(group => new { group.Key.OwnerId, group.Key.CompanyId, Count = group.Count() })
                    .ToListAsync())
                .GroupBy(entry => entry.OwnerId)
                .ToList();

            var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);

            var sharesOwnedByParticipant = holdingsByOwner.ToDictionary(
                group => group.Key,
                group => group.Sum(holding => holding.Count));

            // Estimated market value of a trader's shares: each holding valued at its company's latest price.
            var holdingsValueByParticipant = holdingsByOwner.ToDictionary(
                group => group.Key,
                group => group.Sum(holding => holding.Count * latestPriceByCompany.GetValueOrDefault(holding.CompanyId)));

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
                    holdingsValueByParticipant.GetValueOrDefault(participant.Id),
                    participant.IsActive,
                    participant.IsBankrupt))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/participants/{participantId:int}", async (int participantId, AppDbContext dbContext) =>
        {
            var detail = await BuildParticipantDetailAsync(dbContext, participantId);
            return detail is null
                ? Results.NotFound(new { error = "Participant not found." })
                : Results.Ok(detail);
        });

        app.MapPut("/participants/{participantId:int}/profile", async (
            int participantId,
            UpdateParticipantProfileRequest request,
            MarketService marketService,
            AppDbContext dbContext) =>
        {
            var updated = await marketService.UpdateParticipantProfileAsync(
                participantId,
                request.Temperament,
                request.RiskProfile);

            return updated is null
                ? Results.NotFound(new { error = "Participant not found." })
                : Results.Ok(await BuildParticipantDetailAsync(dbContext, participantId));
        });

        app.MapGet("/participants/{participantId:int}/holdings", async (int participantId, AppDbContext dbContext) =>
        {
            // Share.CurrentPrice is the execution price the share last transferred at, so for a currently
            // held share it equals what this owner paid — summing it per company gives the cost basis.
            var sharesByCompany = await dbContext.Shares
                .Where(share => share.OwnerId == participantId)
                .GroupBy(share => share.CompanyId)
                .Select(group => new { CompanyId = group.Key, Shares = group.Count(), CostBasis = group.Sum(share => share.CurrentPrice) })
                .ToListAsync();

            var companyNameById = await dbContext.Companies
                .ToDictionaryAsync(company => company.Id, company => company.Name);
            var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);

            var response = sharesByCompany
                .OrderByDescending(holding => holding.Shares)
                .Select(holding =>
                {
                    var currentPrice = latestPriceByCompany.GetValueOrDefault(holding.CompanyId);
                    return new HoldingResponse(
                        holding.CompanyId,
                        companyNameById.GetValueOrDefault(holding.CompanyId, $"#{holding.CompanyId}"),
                        holding.Shares,
                        currentPrice,
                        currentPrice * holding.Shares,
                        holding.CostBasis);
                })
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/participants/{participantId:int}/orders", async (int participantId, int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 10, 1, 100);
            var orders = await dbContext.Orders
                .Where(order => order.ParticipantId == participantId)
                .OrderByDescending(order => order.Id)
                .Take(limit)
                .ToListAsync();

            return Results.Ok(orders.Select(ToOrderResponse).ToArray());
        });

        app.MapGet("/participants/{participantId:int}/share-transactions", async (int participantId, int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 10, 1, 100);
            var transactions = await dbContext.ShareTransactions
                .Where(transaction => transaction.SellerId == participantId || transaction.BuyerId == participantId)
                .OrderByDescending(transaction => transaction.Id)
                .Take(limit)
                .ToListAsync();

            return Results.Ok(transactions.Select(ToShareTransactionResponse).ToArray());
        });

        app.MapGet("/participants/{participantId:int}/money-transactions", async (int participantId, int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 10, 1, 100);
            var transactions = await dbContext.MoneyTransactions
                .Where(transaction => transaction.ParticipantId == participantId)
                .OrderByDescending(transaction => transaction.Id)
                .Take(limit)
                .ToListAsync();

            var response = transactions
                .Select(transaction => new MoneyTransactionResponse(
                    transaction.Id,
                    transaction.Type.ToString(),
                    transaction.Amount,
                    transaction.RelatedOrderId,
                    transaction.RelatedShareTransactionId,
                    transaction.CreatedInCycleId,
                    transaction.CreatedAt))
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

            var dividendCycleIds = (await dbContext.MoneyTransactions
                    .Where(transaction => transaction.Type == MoneyTransactionType.Dividend)
                    .Select(transaction => transaction.CreatedInCycleId)
                    .Distinct()
                    .ToListAsync())
                .ToHashSet();

            var cycles = await dbContext.MarketCycles.OrderBy(cycle => cycle.CycleNumber).ToListAsync();
            var response = cycles
                .Select(cycle => new ActivityPointResponse(
                    cycle.CycleNumber,
                    ordersPlacedByCycleId.GetValueOrDefault(cycle.Id),
                    dividendCycleIds.Contains(cycle.Id)))
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

            return Results.Ok(transactions.Select(ToShareTransactionResponse).ToArray());
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

        app.MapGet("/news", async (int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 30, 1, 200);
            var posts = await dbContext.NewsPosts
                .OrderByDescending(post => post.Id)
                .Take(limit)
                .Include(post => post.Industries)
                .ToListAsync();

            var companyNameById = await dbContext.Companies
                .ToDictionaryAsync(company => company.Id, company => company.Name);
            var industryNameById = await IndustryNameByIdAsync(dbContext);

            var response = posts
                .Select(post => ToNewsResponse(post, companyNameById, industryNameById))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapPost("/news", async (ManualNewsRequest request, NewsService newsService, AppDbContext dbContext) =>
        {
            var result = await newsService.PublishManualNewsAsync(request);
            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error });
            }

            var companyNameById = await dbContext.Companies
                .ToDictionaryAsync(company => company.Id, company => company.Name);
            var industryNameById = await IndustryNameByIdAsync(dbContext);

            return Results.Ok(ToNewsResponse(result.Post!, companyNameById, industryNameById));
        });

        app.MapGet("/news/themes", () =>
            Results.Ok(DemoNewsContent.ThemeOptions
                .Select(theme => new NewsThemeResponse(theme.Key, theme.Label))
                .ToArray()));

        app.MapGet("/industries", async (AppDbContext dbContext) =>
        {
            var industries = await dbContext.Industries
                .OrderBy(industry => industry.Name)
                .Select(industry => new IndustryResponse(industry.Id, industry.Name))
                .ToArrayAsync();

            return Results.Ok(industries);
        });

        app.MapGet("/crises", async (int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 30, 1, 200);
            var crises = await dbContext.Crises
                .OrderByDescending(crisis => crisis.Id)
                .Take(limit)
                .Include(crisis => crisis.Industries)
                .ToListAsync();

            var industryNameById = await IndustryNameByIdAsync(dbContext);
            var cycleNumberById = await dbContext.MarketCycles
                .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

            var response = crises
                .Select(crisis => ToCrisisResponse(crisis, industryNameById, cycleNumberById))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/science-investigations", async (int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 30, 1, 200);
            var investigations = await dbContext.ScienceInvestigations
                .OrderByDescending(investigation => investigation.Id)
                .Take(limit)
                .Include(investigation => investigation.Industries)
                .ToListAsync();

            var industryNameById = await IndustryNameByIdAsync(dbContext);
            var cycleNumberById = await dbContext.MarketCycles
                .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

            var response = investigations
                .Select(investigation => ToScienceInvestigationResponse(investigation, industryNameById, cycleNumberById))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/bankruptcies", async (int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 30, 1, 200);
            var bankruptcies = await dbContext.Bankruptcies
                .OrderByDescending(bankruptcy => bankruptcy.Id)
                .Take(limit)
                .ToListAsync();

            var participantIds = bankruptcies.Select(bankruptcy => bankruptcy.ParticipantId).Distinct().ToList();
            var participantNameById = await dbContext.Participants
                .Where(participant => participantIds.Contains(participant.Id))
                .ToDictionaryAsync(participant => participant.Id, participant => participant.Name);
            var cycleNumberById = await dbContext.MarketCycles
                .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

            var response = bankruptcies
                .Select(bankruptcy => ToBankruptcyResponse(bankruptcy, participantNameById, cycleNumberById))
                .ToArray();

            return Results.Ok(response);
        });
    }

    private static async Task<Dictionary<int, string>> IndustryNameByIdAsync(AppDbContext dbContext) =>
        await dbContext.Industries.ToDictionaryAsync(industry => industry.Id, industry => industry.Name);

    private static NewsPostResponse ToNewsResponse(
        NewsPost post,
        IReadOnlyDictionary<int, string> companyNameById,
        IReadOnlyDictionary<int, string> industryNameById) =>
        new(
            post.Id,
            post.Title,
            post.Content,
            post.PublishedInCycleId,
            post.PublishedAt,
            post.Scope.ToString(),
            post.Direction?.ToString(),
            post.ImpactPercent,
            post.TargetCompanyId,
            post.TargetCompanyId is int companyId ? companyNameById.GetValueOrDefault(companyId) : null,
            post.Industries
                .Select(link => industryNameById.GetValueOrDefault(link.IndustryId) ?? $"#{link.IndustryId}")
                .ToArray());

    private static CrisisResponse ToCrisisResponse(
        Crisis crisis,
        IReadOnlyDictionary<int, string> industryNameById,
        IReadOnlyDictionary<int, int> cycleNumberById) =>
        new(
            crisis.Id,
            crisis.Title,
            crisis.Content,
            crisis.Scope.ToString(),
            crisis.TriggeredInCycleId,
            cycleNumberById.GetValueOrDefault(crisis.TriggeredInCycleId),
            crisis.TriggeredAt,
            crisis.Industries
                .Select(link => new CrisisIndustryResponse(
                    link.IndustryId,
                    industryNameById.GetValueOrDefault(link.IndustryId) ?? $"#{link.IndustryId}",
                    link.ImpactPercent))
                .ToArray());

    private static ScienceInvestigationResponse ToScienceInvestigationResponse(
        ScienceInvestigation investigation,
        IReadOnlyDictionary<int, string> industryNameById,
        IReadOnlyDictionary<int, int> cycleNumberById) =>
        new(
            investigation.Id,
            investigation.Title,
            investigation.Content,
            investigation.TriggeredInCycleId,
            cycleNumberById.GetValueOrDefault(investigation.TriggeredInCycleId),
            investigation.TriggeredAt,
            investigation.Industries
                .Select(link => new ScienceInvestigationIndustryResponse(
                    link.IndustryId,
                    industryNameById.GetValueOrDefault(link.IndustryId) ?? $"#{link.IndustryId}",
                    link.ImpactPercent))
                .ToArray());

    private static BankruptcyResponse ToBankruptcyResponse(
        Bankruptcy bankruptcy,
        IReadOnlyDictionary<int, string> participantNameById,
        IReadOnlyDictionary<int, int> cycleNumberById) =>
        new(
            bankruptcy.Id,
            bankruptcy.ParticipantId,
            participantNameById.GetValueOrDefault(bankruptcy.ParticipantId) ?? $"#{bankruptcy.ParticipantId}",
            bankruptcy.Title,
            bankruptcy.Content,
            bankruptcy.CashLost,
            bankruptcy.ShareWorth,
            bankruptcy.TriggeredInCycleId,
            cycleNumberById.GetValueOrDefault(bankruptcy.TriggeredInCycleId),
            bankruptcy.TriggeredAt);

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

    // Change since the prior cycle's close, matching the signal the decision engine reads: the newest
    // price versus the newest from an earlier cycle, or zero when there is no earlier point.
    private static async Task<Dictionary<int, decimal>> PriceChangePctByCompanyAsync(AppDbContext dbContext)
    {
        var snapshots = await dbContext.PriceSnapshots
            .Select(snapshot => new { snapshot.CompanyId, snapshot.Id, snapshot.Price, snapshot.CreatedInCycleId })
            .ToListAsync();

        return snapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var ordered = group.OrderByDescending(snapshot => snapshot.Id).ToList();
                    var latest = ordered[0];
                    var prior = ordered.FirstOrDefault(snapshot => snapshot.CreatedInCycleId != latest.CreatedInCycleId);
                    return prior is { Price: > 0m } ? (latest.Price - prior.Price) / prior.Price : 0m;
                });
    }

    // Total paid to all shareholders in the most recent dividend cycle; zero before any dividend.
    private static async Task<decimal> LastDividendTotalAsync(AppDbContext dbContext)
    {
        var lastDividendCycleId = await dbContext.MoneyTransactions
            .Where(transaction => transaction.Type == MoneyTransactionType.Dividend)
            .OrderByDescending(transaction => transaction.CreatedInCycleId)
            .Select(transaction => (int?)transaction.CreatedInCycleId)
            .FirstOrDefaultAsync();

        if (lastDividendCycleId is null)
        {
            return 0m;
        }

        return await dbContext.MoneyTransactions
            .Where(transaction => transaction.Type == MoneyTransactionType.Dividend
                && transaction.CreatedInCycleId == lastDividendCycleId)
            .SumAsync(transaction => transaction.Amount);
    }

    private static async Task<CompanyDetailResponse?> BuildCompanyDetailAsync(AppDbContext dbContext, int companyId)
    {
        var company = await dbContext.Companies.FirstOrDefaultAsync(candidate => candidate.Id == companyId);
        if (company is null)
        {
            return null;
        }

        // Shares the issuer has not yet sold carry no owner; the rest are outstanding in participants' hands.
        var sharesHeldByIssuer = await dbContext.Shares
            .CountAsync(share => share.CompanyId == companyId && share.OwnerId == null);
        var sharesOutstanding = await dbContext.Shares
            .CountAsync(share => share.CompanyId == companyId && share.OwnerId != null);
        var shareholderCount = await dbContext.Shares
            .Where(share => share.CompanyId == companyId && share.OwnerId != null)
            .Select(share => share.OwnerId)
            .Distinct()
            .CountAsync();

        var currentPrice = (await LatestPriceByCompanyAsync(dbContext)).GetValueOrDefault(companyId);
        var priceChangePct = (await PriceChangePctByCompanyAsync(dbContext)).GetValueOrDefault(companyId);
        var industryName = await dbContext.Industries
            .Where(industry => industry.Id == company.IndustryId)
            .Select(industry => industry.Name)
            .FirstOrDefaultAsync();

        return new CompanyDetailResponse(
            company.Id,
            company.Name,
            company.IndustryId,
            industryName,
            company.IssuedSharesCount,
            currentPrice == 0m ? null : currentPrice,
            priceChangePct,
            currentPrice * company.IssuedSharesCount,
            sharesHeldByIssuer,
            sharesOutstanding,
            shareholderCount,
            company.CreatedAt);
    }

    private static async Task<ParticipantDetailResponse?> BuildParticipantDetailAsync(AppDbContext dbContext, int participantId)
    {
        var participant = await dbContext.Participants.FirstOrDefaultAsync(candidate => candidate.Id == participantId);
        if (participant is null)
        {
            return null;
        }

        var sharesOwned = await dbContext.Shares.CountAsync(share => share.OwnerId == participantId);

        return new ParticipantDetailResponse(
            participant.Id,
            participant.Name,
            participant.Type.ToString(),
            participant.Temperament.ToString(),
            participant.RiskProfile.ToString(),
            participant.InitialBalance,
            participant.CurrentBalance,
            participant.ReservedBalance,
            participant.AvailableBalance,
            sharesOwned,
            participant.IsActive);
    }

    private static ShareTransactionResponse ToShareTransactionResponse(ShareTransaction transaction) => new(
        transaction.Id,
        transaction.SellerId,
        transaction.BuyerId,
        transaction.CompanyId,
        transaction.Quantity,
        transaction.Price,
        transaction.TotalCost,
        transaction.CreatedInCycleId,
        transaction.CreatedAt);

    private static MarketResponse ToMarketResponse(
        Market market,
        decimal lastDividendTotal = 0m,
        int? currentCycleNumber = null) =>
        new(
            market.Id,
            market.Name,
            market.Status.ToString(),
            market.CurrentCycleId,
            currentCycleNumber,
            lastDividendTotal);

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

public sealed record CompanyResponse(
    int Id,
    string Name,
    int IndustryId,
    string? IndustryName,
    int IssuedSharesCount,
    decimal? CurrentPrice,
    decimal PriceChangePct);

public sealed record CompanyDetailResponse(
    int Id,
    string Name,
    int IndustryId,
    string? IndustryName,
    int IssuedSharesCount,
    decimal? CurrentPrice,
    decimal PriceChangePct,
    decimal MarketCap,
    int SharesHeldByIssuer,
    int SharesOutstanding,
    int ShareholderCount,
    DateTime CreatedAt);

public sealed record ShareholderResponse(
    int OwnerId,
    string OwnerName,
    int Shares,
    decimal MarketValue,
    decimal CostBasis,
    decimal PctOfIssued);

public sealed record MarketResponse(
    int Id,
    string Name,
    string Status,
    int? CurrentCycleId,
    int? CurrentCycleNumber,
    decimal LastDividendTotal);

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
    decimal HoldingsValue,
    bool IsActive,
    bool IsBankrupt);

public sealed record ParticipantDetailResponse(
    int Id,
    string Name,
    string Type,
    string Temperament,
    string RiskProfile,
    decimal InitialBalance,
    decimal CurrentBalance,
    decimal ReservedBalance,
    decimal AvailableBalance,
    int SharesOwned,
    bool IsActive);

public sealed record UpdateParticipantProfileRequest(Temperament Temperament, RiskProfile RiskProfile);

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

public sealed record ActivityPointResponse(int CycleNumber, int OrdersPlaced, bool PaidDividend);

public sealed record HoldingResponse(
    int CompanyId,
    string CompanyName,
    int Shares,
    decimal CurrentPrice,
    decimal MarketValue,
    decimal CostBasis);

public sealed record MoneyTransactionResponse(
    int Id,
    string Type,
    decimal Amount,
    int? RelatedOrderId,
    int? RelatedShareTransactionId,
    int CreatedInCycleId,
    DateTime CreatedAt);

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

public sealed record NewsPostResponse(
    int Id,
    string Title,
    string Content,
    int PublishedInCycleId,
    DateTime PublishedAt,
    string Scope,
    string? Direction,
    decimal? ImpactPercent,
    int? TargetCompanyId,
    string? TargetCompanyName,
    string[] IndustryNames);

public sealed record IndustryResponse(int Id, string Name);

public sealed record NewsThemeResponse(string Key, string Label);

public sealed record CrisisResponse(
    int Id,
    string Title,
    string Content,
    string Scope,
    int TriggeredInCycleId,
    int TriggeredInCycleNumber,
    DateTime TriggeredAt,
    CrisisIndustryResponse[] Industries);

public sealed record CrisisIndustryResponse(int IndustryId, string IndustryName, decimal ImpactPercent);

public sealed record ScienceInvestigationResponse(
    int Id,
    string Title,
    string Content,
    int TriggeredInCycleId,
    int TriggeredInCycleNumber,
    DateTime TriggeredAt,
    ScienceInvestigationIndustryResponse[] Industries);

public sealed record ScienceInvestigationIndustryResponse(int IndustryId, string IndustryName, decimal ImpactPercent);

public sealed record BankruptcyResponse(
    int Id,
    int ParticipantId,
    string ParticipantName,
    string Title,
    string Content,
    decimal CashLost,
    decimal ShareWorth,
    int TriggeredInCycleId,
    int TriggeredInCycleNumber,
    DateTime TriggeredAt);
