using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Api;

public static partial class MarketEndpoints
{
    public static void MapCompanyEndpoints(this WebApplication app)
    {
        app.MapGet("/companies", async (AppDbContext dbContext, IOptions<VolatilityHaltOptions> haltOptions) =>
            Results.Ok(await BuildCompanyResponsesAsync(dbContext, haltOptions.Value)));

        // Server-paged companies for the roster page: name search, industry filter, and sortable numeric
        // columns. The array endpoint above still feeds the dashboard map, which needs the whole set.
        app.MapGet("/companies/paged", async (
            int? page, int? pageSize, string? search, string? sort, string? sortDir, int? industryId,
            AppDbContext dbContext, IOptions<VolatilityHaltOptions> haltOptions) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 20);
            var descending = SortDescending(sortDir);

            IEnumerable<CompanyResponse> companies = await BuildCompanyResponsesAsync(dbContext, haltOptions.Value);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLowerInvariant();
                companies = companies.Where(company => company.Name.ToLowerInvariant().Contains(term));
            }
            if (industryId is int industry)
            {
                companies = companies.Where(company => company.IndustryId == industry);
            }

            var filtered = companies.ToList();
            IEnumerable<CompanyResponse> ordered = sort switch
            {
                "name" => descending
                    ? filtered.OrderByDescending(company => company.Name, StringComparer.OrdinalIgnoreCase)
                    : filtered.OrderBy(company => company.Name, StringComparer.OrdinalIgnoreCase),
                "shares" => OrderRows(filtered, company => company.IssuedSharesCount, descending),
                "price" => OrderRows(filtered, company => company.CurrentPrice ?? 0m, descending),
                _ => OrderRows(filtered, company => company.IssuedSharesCount * (company.CurrentPrice ?? 0m), descending),
            };

            var items = ordered.Skip((pageIndex - 1) * size).Take(size).ToArray();
            return Results.Ok(new PagedCompaniesResponse(items, filtered.Count, pageIndex, size));
        });

        app.MapGet("/companies/{companyId:int}", async (int companyId, AppDbContext dbContext, IOptions<VolatilityHaltOptions> haltOptions) =>
        {
            var detail = await BuildCompanyDetailAsync(dbContext, companyId, haltOptions.Value);
            return detail is null
                ? Results.NotFound(new { error = "Company not found." })
                : Results.Ok(detail);
        });

        app.MapGet("/companies/{companyId:int}/corporate-cash-movements", async (
            int companyId, int? page, int? pageSize, AppDbContext dbContext) =>
        {
            if (!await dbContext.Companies.AnyAsync(company => company.Id == companyId))
            {
                return Results.NotFound(new { error = "Company not found." });
            }

            var (pageIndex, size) = ResolvePaging(page, pageSize, 10);
            var query = dbContext.CorporateCashTransactions.Where(transaction => transaction.CompanyId == companyId);
            var total = await query.CountAsync();
            var rows = await query
                .OrderByDescending(transaction => transaction.Id)
                .Skip((pageIndex - 1) * size)
                .Take(size)
                .ToListAsync();
            var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);
            var items = rows
                .Select(transaction => new CorporateCashMovementResponse(
                    transaction.Id,
                    transaction.Type.ToString(),
                    transaction.Amount,
                    transaction.CreatedInCycleId,
                    cycleNumbersById.GetValueOrDefault(transaction.CreatedInCycleId),
                    transaction.CreatedAt))
                .ToArray();

            return Results.Ok(new PagedCorporateCashMovementsResponse(items, total, pageIndex, size));
        });

        app.MapGet("/companies/{companyId:int}/shareholders", async (int companyId, AppDbContext dbContext) =>
        {
            // AverageCost is the weighted-average price the owner paid, so cost basis is quantity times it.
            var sharesByOwner = await dbContext.Holdings
                .Where(holding => holding.CompanyId == companyId && holding.Quantity > 0)
                .Select(holding => new { OwnerId = holding.ParticipantId, Shares = holding.Quantity, CostBasis = holding.Quantity * holding.AverageCost })
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

            var ownerNames = await ParticipantNamesAsync(
                dbContext, orders.Where(order => order.ParticipantId != null).Select(order => order.ParticipantId!.Value));

            return Results.Ok(orders
                .Select(order => ToOrderResponse(order) with
                {
                    ParticipantName = order.ParticipantId is int ownerId ? ownerNames.GetValueOrDefault(ownerId) : null,
                })
                .ToArray());
        });

        app.MapGet("/companies/{companyId:int}/share-transactions", async (int companyId, int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 10, 1, 100);
            var transactions = await dbContext.ShareTransactions
                .Where(transaction => transaction.CompanyId == companyId)
                .Include(transaction => transaction.SettlementInstruction)
                .OrderByDescending(transaction => transaction.Id)
                .Take(limit)
                .ToListAsync();

            var partyIds = transactions
                .SelectMany(transaction => transaction.SellerId is int sellerId
                    ? new[] { sellerId, transaction.BuyerId }
                    : new[] { transaction.BuyerId });
            var partyNames = await ParticipantNamesAsync(dbContext, partyIds);
            var priceBefore = await MarketPriceBeforeTradesAsync(dbContext, companyId, transactions);

            return Results.Ok(transactions
                .Select(transaction => ToShareTransactionResponse(transaction) with
                {
                    SellerName = transaction.SellerId is int sellerId ? partyNames.GetValueOrDefault(sellerId) : null,
                    BuyerName = partyNames.GetValueOrDefault(transaction.BuyerId),
                    MarketPriceBefore = priceBefore.TryGetValue(transaction.Id, out var price) ? price : null,
                })
                .ToArray());
        });

        app.MapGet("/companies/{companyId:int}/ratings", async (int companyId, int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 20, 1, 100);
            var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);
            var currentCycleNumber = await CurrentCycleNumberAsync(dbContext);
            var auditorNameById = await dbContext.Auditors.ToDictionaryAsync(auditor => auditor.Id, auditor => auditor.Name);

            var ratings = await dbContext.CompanyRatings
                .Where(rating => rating.CompanyId == companyId)
                .OrderByDescending(rating => rating.Id)
                .Take(limit)
                .ToListAsync();

            var response = ratings
                .Select(rating => new CompanyRatingResponse(
                    rating.Id,
                    rating.Rating.ToString(),
                    rating.ImpactPercent,
                    auditorNameById.GetValueOrDefault(rating.AuditorId, $"#{rating.AuditorId}"),
                    Math.Max(0, currentCycleNumber - cycleNumbersById.GetValueOrDefault(rating.CreatedInCycleId)),
                    rating.CreatedAt))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/companies/{companyId:int}/emissions", async (int companyId, int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 20, 1, 100);
            var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);
            var currentCycleNumber = await CurrentCycleNumberAsync(dbContext);
            var currentRunId = await dbContext.Markets.Select(market => market.CurrentRunId).SingleOrDefaultAsync();

            var emissions = await dbContext.ShareEmissions
                .Where(emission => emission.CompanyId == companyId
                    && (emission.MarketRunId == currentRunId || emission.MarketRunId == null))
                .OrderByDescending(emission => emission.Id)
                .Take(limit)
                .ToListAsync();

            var response = emissions
                .Select(emission => new ShareEmissionResponse(
                    emission.Id,
                    emission.SharesEmitted,
                    emission.RecipientCount,
                    Math.Max(0, currentCycleNumber - cycleNumbersById.GetValueOrDefault(emission.CreatedInCycleId)),
                    emission.CreatedAt))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/companies/{companyId:int}/investments", async (int companyId, int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 20, 1, 100);
            var currentRunId = await dbContext.Markets.Select(market => market.CurrentRunId).SingleOrDefaultAsync();
            var investments = await dbContext.CompanyInvestments
                .Where(investment => investment.CompanyId == companyId
                    && (investment.MarketRunId == currentRunId || investment.MarketRunId == null))
                .OrderByDescending(investment => investment.Id)
                .Take(limit)
                .ToListAsync();

            return Results.Ok(await ToInvestmentResponsesAsync(dbContext, investments));
        });

        // News related to one company: posts that target it directly plus industry-scoped posts that hit the
        // company's industry, newest first.
        app.MapGet("/companies/{companyId:int}/news", async (int companyId, int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 20, 1, 100);
            var industryId = await dbContext.Companies
                .Where(company => company.Id == companyId)
                .Select(company => (int?)company.IndustryId)
                .FirstOrDefaultAsync();
            if (industryId is null)
            {
                return Results.NotFound(new { error = "Company not found." });
            }

            var posts = await dbContext.NewsPosts
                .Where(post => post.TargetCompanyId == companyId
                    || (post.Scope == NewsImpactScope.Industries
                        && post.Industries.Any(link => link.IndustryId == industryId)))
                .OrderByDescending(post => post.Id)
                .Take(limit)
                .Include(post => post.Industries)
                .ToListAsync();

            var companyNameById = await dbContext.Companies
                .ToDictionaryAsync(company => company.Id, company => company.Name);
            var industryNameById = await IndustryNameByIdAsync(dbContext);
            var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);

            var response = posts
                .Select(post => ToNewsResponse(post, companyNameById, industryNameById, cycleNumbersById))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapPost("/companies/{companyId:int}/invest", async (int companyId, InvestInCompanyRequest request, MarketService marketService) =>
        {
            var result = await marketService.InvestInCompanyAsync(request.ParticipantId, companyId, request.Amount);
            return result.Success
                ? Results.Ok(new InvestInCompanyResponse(result.SharesMinted))
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapGet("/companies/closed", async (int? page, int? pageSize, AppDbContext dbContext) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 20);

            var query = dbContext.Companies.Where(company => company.ClosedInCycleId != null);
            var total = await query.CountAsync();

            var companies = await query
                .OrderByDescending(company => company.ClosedAt)
                .ThenByDescending(company => company.Id)
                .Skip((pageIndex - 1) * size)
                .Take(size)
                .ToListAsync();

            var industryNameById = await IndustryNameByIdAsync(dbContext);
            var cycleNumberById = await dbContext.MarketCycles
                .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);
            // A delisted company's last snapshot is the price it closed at.
            var finalPriceByCompany = await LatestPriceByCompanyAsync(dbContext);

            var items = companies
                .Select(company => new ClosedCompanyResponse(
                    company.Id,
                    company.Name,
                    company.IndustryId,
                    industryNameById.GetValueOrDefault(company.IndustryId),
                    company.IssuedSharesCount,
                    finalPriceByCompany.GetValueOrDefault(company.Id),
                    company.CreatedInCycleId is int createdCycleId ? cycleNumberById.GetValueOrDefault(createdCycleId) : 0,
                    company.ClosedInCycleId is int closedCycleId ? cycleNumberById.GetValueOrDefault(closedCycleId) : 0,
                    company.ClosedAt))
                .ToArray();

            return Results.Ok(new PagedClosedCompaniesResponse(items, total, pageIndex, size));
        });

        app.MapGet("/auditors", async (AppDbContext dbContext) =>
        {
            var auditors = await dbContext.Auditors.OrderBy(auditor => auditor.Id).ToListAsync();
            var countByAuditor = (await dbContext.CompanyRatings
                    .GroupBy(rating => rating.AuditorId)
                    .Select(group => new { AuditorId = group.Key, Count = group.Count() })
                    .ToListAsync())
                .ToDictionary(row => row.AuditorId, row => row.Count);

            var response = auditors
                .Select(auditor => new AuditorResponse(
                    auditor.Id, auditor.Name, auditor.Description, countByAuditor.GetValueOrDefault(auditor.Id)))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/auditors/{auditorId:int}", async (int auditorId, AppDbContext dbContext) =>
        {
            var auditor = await dbContext.Auditors.FirstOrDefaultAsync(candidate => candidate.Id == auditorId);
            if (auditor is null)
            {
                return Results.NotFound(new { error = "Auditor not found." });
            }

            var count = await dbContext.CompanyRatings.CountAsync(rating => rating.AuditorId == auditorId);
            return Results.Ok(new AuditorResponse(auditor.Id, auditor.Name, auditor.Description, count));
        });

        app.MapGet("/auditors/{auditorId:int}/audits", async (int auditorId, int? page, int? pageSize, AppDbContext dbContext) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 20);

            var query = dbContext.CompanyRatings.Where(rating => rating.AuditorId == auditorId);
            var total = await query.CountAsync();

            var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);
            var currentCycleNumber = await CurrentCycleNumberAsync(dbContext);
            var companyNameById = await dbContext.Companies.ToDictionaryAsync(company => company.Id, company => company.Name);

            var rows = await query
                .OrderByDescending(rating => rating.Id)
                .Skip((pageIndex - 1) * size)
                .Take(size)
                .ToListAsync();

            var items = rows
                .Select(rating => new AuditRowResponse(
                    rating.Id,
                    rating.CompanyId,
                    companyNameById.GetValueOrDefault(rating.CompanyId, $"#{rating.CompanyId}"),
                    rating.Rating.ToString(),
                    rating.ImpactPercent,
                    Math.Max(0, currentCycleNumber - cycleNumbersById.GetValueOrDefault(rating.CreatedInCycleId)),
                    rating.CreatedAt))
                .ToArray();

            return Results.Ok(new PagedAuditsResponse(items, total, pageIndex, size));
        });
    }
}
