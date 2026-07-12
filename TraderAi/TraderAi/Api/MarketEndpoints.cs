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
            Results.Ok(await BuildCompanyResponsesAsync(dbContext)));

        // Server-paged companies for the roster page: name search, industry filter, and sortable numeric
        // columns. The array endpoint above still feeds the dashboard map, which needs the whole set.
        app.MapGet("/companies/paged", async (
            int? page, int? pageSize, string? search, string? sort, string? sortDir, int? industryId,
            AppDbContext dbContext) =>
        {
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var pageIndex = Math.Max(page ?? 1, 1);
            var descending = !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);

            IEnumerable<CompanyResponse> companies = await BuildCompanyResponsesAsync(dbContext);
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
                "shares" => Order(filtered, company => company.IssuedSharesCount, descending),
                "price" => Order(filtered, company => company.CurrentPrice ?? 0m, descending),
                _ => Order(filtered, company => company.IssuedSharesCount * (company.CurrentPrice ?? 0m), descending),
            };

            var items = ordered.Skip((pageIndex - 1) * size).Take(size).ToArray();
            return Results.Ok(new PagedCompaniesResponse(items, filtered.Count, pageIndex, size));

            static IEnumerable<CompanyResponse> Order(IEnumerable<CompanyResponse> source, Func<CompanyResponse, decimal> key, bool desc) =>
                desc ? source.OrderByDescending(key) : source.OrderBy(key);
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

            var emissions = await dbContext.ShareEmissions
                .Where(emission => emission.CompanyId == companyId)
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
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var pageIndex = Math.Max(page ?? 1, 1);

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
            Results.Ok(await BuildParticipantResponsesAsync(dbContext)));

        // Server-paged traders for the roster page: name search, type filter, and sortable numeric columns.
        // The array endpoint above still feeds the dashboard, which sums trader cash across the whole set.
        app.MapGet("/participants/paged", async (
            int? page, int? pageSize, string? search, string? sort, string? sortDir, string? type,
            AppDbContext dbContext) =>
        {
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var pageIndex = Math.Max(page ?? 1, 1);
            var descending = !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);

            IEnumerable<ParticipantResponse> participants = await BuildParticipantResponsesAsync(dbContext);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLowerInvariant();
                participants = participants.Where(participant => participant.Name.ToLowerInvariant().Contains(term));
            }
            if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "all", StringComparison.OrdinalIgnoreCase))
            {
                participants = participants.Where(participant => participant.Type == type);
            }

            var filtered = participants.ToList();
            IEnumerable<ParticipantResponse> ordered = sort switch
            {
                "name" => descending
                    ? filtered.OrderByDescending(participant => participant.Name, StringComparer.OrdinalIgnoreCase)
                    : filtered.OrderBy(participant => participant.Name, StringComparer.OrdinalIgnoreCase),
                "shares" => Order(filtered, participant => participant.SharesOwned, descending),
                "balance" => Order(filtered, participant => participant.CurrentBalance, descending),
                "holdings" => Order(filtered, participant => participant.HoldingsValue, descending),
                _ => Order(filtered, participant => participant.CurrentBalance + participant.HoldingsValue - participant.LoanLiability, descending),
            };

            var items = ordered.Skip((pageIndex - 1) * size).Take(size).ToArray();
            return Results.Ok(new PagedParticipantsResponse(items, filtered.Count, pageIndex, size));

            static IEnumerable<ParticipantResponse> Order(IEnumerable<ParticipantResponse> source, Func<ParticipantResponse, decimal> key, bool desc) =>
                desc ? source.OrderByDescending(key) : source.OrderBy(key);
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
            // AverageCost is the weighted-average price this owner paid for the position, so cost basis is
            // quantity times it.
            var sharesByCompany = await dbContext.Holdings
                .Where(holding => holding.ParticipantId == participantId && holding.Quantity > 0)
                .Select(holding => new { holding.CompanyId, Shares = holding.Quantity, CostBasis = holding.Quantity * holding.AverageCost })
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

        // Companies the participant holds that show a warning signal, so they can be watched at a glance: a recent
        // price slide, a bad news or crisis hit, a standing High/Extra risk verdict, or a recent reverse merge.
        app.MapGet("/participants/{participantId:int}/companies-attention", async (int participantId, AppDbContext dbContext) =>
        {
            const int RecentWindowCycles = 20;
            const int PriceTrendWindowCycles = 10;
            const int PriceDeclineThreshold = 3;

            var heldQuantityByCompany = await dbContext.Holdings
                .Where(holding => holding.ParticipantId == participantId && holding.Quantity > 0)
                .ToDictionaryAsync(holding => holding.CompanyId, holding => holding.Quantity);
            if (heldQuantityByCompany.Count == 0)
            {
                return Results.Ok(Array.Empty<CompanyAttentionResponse>());
            }

            var heldCompanyIds = heldQuantityByCompany.Keys.ToList();
            var companies = await dbContext.Companies
                .Where(company => heldCompanyIds.Contains(company.Id) && company.ClosedInCycleId == null)
                .ToListAsync();

            var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);
            var currentCycleNumber = await CurrentCycleNumberAsync(dbContext);
            var recentCycleIds = cycleNumbersById
                .Where(entry => entry.Value > 0 && currentCycleNumber - entry.Value < RecentWindowCycles)
                .Select(entry => entry.Key)
                .ToHashSet();

            var industryNameById = await IndustryNameByIdAsync(dbContext);
            var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);
            var changeByCompany = await PriceChangePctByCompanyAsync(dbContext);
            var latestRatingByCompany = await LatestRatingByCompanyAsync(dbContext);
            var heldIndustryIds = companies.Select(company => company.IndustryId).Distinct().ToList();

            // (a) Price slide: reduce each company's snapshots to one close per cycle, then count cycle-over-cycle
            // declines across the last few cycles.
            var priceRows = await dbContext.PriceSnapshots
                .Where(snapshot => heldCompanyIds.Contains(snapshot.CompanyId)
                    && recentCycleIds.Contains(snapshot.CreatedInCycleId))
                .Select(snapshot => new { snapshot.CompanyId, snapshot.Id, snapshot.Price, snapshot.CreatedInCycleId })
                .ToListAsync();
            var decliningCompanyIds = new HashSet<int>();
            foreach (var group in priceRows.GroupBy(row => row.CompanyId))
            {
                var closes = group
                    .GroupBy(row => row.CreatedInCycleId)
                    .Select(cycleGroup => cycleGroup.OrderByDescending(row => row.Id).First())
                    .Select(row => new { CycleNumber = cycleNumbersById.GetValueOrDefault(row.CreatedInCycleId), row.Price })
                    .OrderBy(entry => entry.CycleNumber)
                    .TakeLast(PriceTrendWindowCycles + 1)
                    .ToList();
                var declines = 0;
                for (var index = 1; index < closes.Count; index++)
                {
                    if (closes[index].Price < closes[index - 1].Price)
                    {
                        declines++;
                    }
                }
                if (declines >= PriceDeclineThreshold)
                {
                    decliningCompanyIds.Add(group.Key);
                }
            }

            // (b) Bad impact: a company-targeted price-down post, an industry-down post on a held industry, or a
            // crisis on a held industry — all always negative — within the recent window.
            var badNewsCompanyIds = new HashSet<int>();
            var companyDownNews = await dbContext.NewsPosts
                .Where(post => post.Direction == NewsImpactDirection.Decrease
                    && post.Scope == NewsImpactScope.Company
                    && post.TargetCompanyId != null
                    && heldCompanyIds.Contains(post.TargetCompanyId.Value)
                    && recentCycleIds.Contains(post.PublishedInCycleId))
                .Select(post => post.TargetCompanyId!.Value)
                .ToListAsync();
            foreach (var companyId in companyDownNews)
            {
                badNewsCompanyIds.Add(companyId);
            }

            var industryDownNews = await dbContext.NewsPosts
                .Where(post => post.Direction == NewsImpactDirection.Decrease
                    && post.Scope == NewsImpactScope.Industries
                    && recentCycleIds.Contains(post.PublishedInCycleId)
                    && post.Industries.Any(link => heldIndustryIds.Contains(link.IndustryId)))
                .Include(post => post.Industries)
                .ToListAsync();
            var crises = await dbContext.Crises
                .Where(crisis => recentCycleIds.Contains(crisis.TriggeredInCycleId)
                    && crisis.Industries.Any(link => heldIndustryIds.Contains(link.IndustryId)))
                .Include(crisis => crisis.Industries)
                .ToListAsync();
            var hitIndustryIds = industryDownNews
                .SelectMany(post => post.Industries.Select(link => link.IndustryId))
                .Concat(crises.SelectMany(crisis => crisis.Industries.Select(link => link.IndustryId)))
                .ToHashSet();
            foreach (var company in companies)
            {
                if (hitIndustryIds.Contains(company.IndustryId))
                {
                    badNewsCompanyIds.Add(company.Id);
                }
            }

            // (c) Standing High/Extra risk: the most recent verdict inside the window is High or Extra (a later Low
            // would be the newest and clear it).
            var highRiskCompanyIds = (await dbContext.CompanyRatings
                    .Where(rating => heldCompanyIds.Contains(rating.CompanyId)
                        && recentCycleIds.Contains(rating.CreatedInCycleId))
                    .Select(rating => new { rating.CompanyId, rating.Id, rating.Rating })
                    .ToListAsync())
                .GroupBy(rating => rating.CompanyId)
                .Where(group => group.OrderByDescending(rating => rating.Id).First().Rating != CompanyRiskRating.Low)
                .Select(group => group.Key)
                .ToHashSet();

            var response = companies
                .Select(company =>
                {
                    var price = latestPriceByCompany.GetValueOrDefault(company.Id);
                    var shares = heldQuantityByCompany.GetValueOrDefault(company.Id);
                    return new CompanyAttentionResponse(
                        company.Id,
                        company.Name,
                        industryNameById.GetValueOrDefault(company.IndustryId),
                        price,
                        changeByCompany.GetValueOrDefault(company.Id),
                        latestRatingByCompany.TryGetValue(company.Id, out var rating) ? rating.ToString() : null,
                        shares,
                        shares * price,
                        decliningCompanyIds.Contains(company.Id),
                        badNewsCompanyIds.Contains(company.Id),
                        highRiskCompanyIds.Contains(company.Id),
                        company.LastMergedInCycleId is int mergedCycleId && recentCycleIds.Contains(mergedCycleId));
                })
                .Where(row => row.PriceDeclining || row.BadNewsImpact || row.HighRisk || row.RecentMerge)
                .OrderByDescending(row => row.MarketValue)
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
                    transaction.RelatedLoanId,
                    transaction.CreatedInCycleId,
                    transaction.CreatedAt))
                .ToArray();

            return Results.Ok(response);
        });

        // One cash movement, enriched on open with whatever it links to: the order and settled trade for a
        // trade-driven row, the loan behind a loan row, or the per-company breakdown behind a dividend. The list
        // endpoint stays lean; this resolves names and related detail only when a row is inspected.
        app.MapGet("/participants/{participantId:int}/money-transactions/{transactionId:int}", async (
            int participantId, int transactionId, AppDbContext dbContext) =>
        {
            var transaction = await dbContext.MoneyTransactions
                .FirstOrDefaultAsync(row => row.Id == transactionId && row.ParticipantId == participantId);
            if (transaction is null)
            {
                return Results.NotFound();
            }

            var cycleNumber = await dbContext.MarketCycles
                .Where(cycle => cycle.Id == transaction.CreatedInCycleId)
                .Select(cycle => (int?)cycle.CycleNumber)
                .FirstOrDefaultAsync();

            var order = transaction.RelatedOrderId is int orderId
                ? await dbContext.Orders.FirstOrDefaultAsync(row => row.Id == orderId)
                : null;
            var trade = transaction.RelatedShareTransactionId is int shareTransactionId
                ? await dbContext.ShareTransactions.FirstOrDefaultAsync(row => row.Id == shareTransactionId)
                : null;
            var loan = transaction.RelatedLoanId is int loanId
                ? await dbContext.Loans.FirstOrDefaultAsync(row => row.Id == loanId)
                : null;
            var dividendLines = transaction.Type == MoneyTransactionType.Dividend
                ? await dbContext.DividendPayouts
                    .Where(payout => payout.MoneyTransactionId == transaction.Id)
                    .OrderByDescending(payout => payout.Amount)
                    .ToListAsync()
                : [];

            // Resolve every referenced company in one query. Closed companies are retained, so historical rows
            // still get their names.
            var companyIds = new List<int>();
            if (order is not null) companyIds.Add(order.CompanyId);
            if (trade is not null) companyIds.Add(trade.CompanyId);
            companyIds.AddRange(dividendLines.Select(payout => payout.CompanyId));
            var companyNameById = companyIds.Count == 0
                ? new Dictionary<int, string>()
                : await dbContext.Companies
                    .Where(company => companyIds.Contains(company.Id))
                    .ToDictionaryAsync(company => company.Id, company => company.Name);

            string? NameOf(int companyId) => companyNameById.GetValueOrDefault(companyId);

            var response = new MoneyTransactionDetailResponse(
                transaction.Id,
                transaction.Type.ToString(),
                transaction.Amount,
                transaction.CreatedInCycleId,
                cycleNumber,
                transaction.CreatedAt,
                order is null
                    ? null
                    : new MoneyTransactionOrderInfo(
                        order.Id,
                        order.CompanyId,
                        NameOf(order.CompanyId),
                        order.Type.ToString(),
                        order.Status.ToString(),
                        order.Quantity,
                        order.FilledQuantity,
                        order.LimitPrice),
                trade is null
                    ? null
                    : new MoneyTransactionTradeInfo(
                        trade.Id,
                        trade.CompanyId,
                        NameOf(trade.CompanyId),
                        trade.Quantity,
                        trade.Price,
                        trade.TotalCost),
                loan is null
                    ? null
                    : new MoneyTransactionLoanInfo(
                        loan.Id,
                        loan.Principal,
                        loan.RemainingPrincipal,
                        loan.InterestRatePerCycle,
                        loan.TermCycles,
                        loan.PastDueAmount,
                        loan.Status.ToString()),
                dividendLines.Count == 0
                    ? null
                    : dividendLines
                        .Select(payout => new DividendPayoutLineResponse(payout.CompanyId, NameOf(payout.CompanyId), payout.Amount))
                        .ToArray());

            return Results.Ok(response);
        });

        app.MapGet("/participants/{participantId:int}/worth-history", async (int participantId, int? take, AppDbContext dbContext) =>
        {
            // Newest first for the cap, then flipped to chronological so the chart reads left-to-right.
            var limit = Math.Clamp(take ?? 200, 1, 1000);
            var snapshots = await dbContext.ParticipantWorthSnapshots
                .Where(snapshot => snapshot.ParticipantId == participantId)
                .OrderByDescending(snapshot => snapshot.Id)
                .Take(limit)
                .ToListAsync();

            var cycleNumberById = await dbContext.MarketCycles
                .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

            var response = snapshots
                .OrderBy(snapshot => snapshot.Id)
                .Select(snapshot => new ParticipantWorthPointResponse(
                    snapshot.CreatedInCycleId,
                    cycleNumberById.GetValueOrDefault(snapshot.CreatedInCycleId),
                    snapshot.Balance,
                    snapshot.HoldingsValue,
                    snapshot.LoanLiability,
                    snapshot.Balance + snapshot.HoldingsValue - snapshot.LoanLiability,
                    snapshot.CreatedAt))
                .ToArray();

            return Results.Ok(response);
        });

        // Fund join/leave history for a participant, serving both sides from one contract: a trader's page sees
        // the funds it joined or left, a fund's page sees the members who joined or left it. Server-paged,
        // newest-first.
        app.MapGet("/participants/{participantId:int}/fund-membership-history", async (
            int participantId, int? page, int? pageSize, AppDbContext dbContext) =>
        {
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var pageIndex = Math.Max(page ?? 1, 1);

            var query = dbContext.CollectiveFundMembershipEvents
                .Where(membershipEvent => membershipEvent.ParticipantId == participantId
                    || membershipEvent.FundParticipantId == participantId);
            var total = await query.CountAsync();

            var events = await query
                .OrderByDescending(membershipEvent => membershipEvent.Id)
                .Skip((pageIndex - 1) * size)
                .Take(size)
                .ToListAsync();

            var participantIds = events
                .SelectMany(membershipEvent => new[] { membershipEvent.ParticipantId, membershipEvent.FundParticipantId })
                .Distinct()
                .ToList();
            var nameById = await dbContext.Participants
                .Where(participant => participantIds.Contains(participant.Id))
                .ToDictionaryAsync(participant => participant.Id, participant => participant.Name);
            var cycleNumberById = await dbContext.MarketCycles
                .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

            var items = events
                .Select(membershipEvent => new FundMembershipEventResponse(
                    membershipEvent.Id,
                    membershipEvent.Type.ToString(),
                    membershipEvent.Amount,
                    membershipEvent.CollectiveFundId,
                    membershipEvent.ParticipantId,
                    nameById.GetValueOrDefault(membershipEvent.ParticipantId, $"#{membershipEvent.ParticipantId}"),
                    membershipEvent.FundParticipantId,
                    nameById.GetValueOrDefault(membershipEvent.FundParticipantId, $"#{membershipEvent.FundParticipantId}"),
                    membershipEvent.CreatedInCycleId,
                    cycleNumberById.GetValueOrDefault(membershipEvent.CreatedInCycleId),
                    membershipEvent.CreatedAt))
                .ToArray();

            return Results.Ok(new PagedFundMembershipEventsResponse(items, total, pageIndex, size));
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

        app.MapGet("/player", async (AppDbContext dbContext) =>
            Results.Ok(await BuildPlayerResponseAsync(dbContext)));

        app.MapPost("/player", async (CreatePlayerRequest? request, MarketService marketService, AppDbContext dbContext) =>
        {
            var result = await marketService.CreatePlayerAsync(request?.Name);
            if (!result.Success)
            {
                // The absent market is a bad request; an existing player is a conflict.
                return result.Error == "No market exists."
                    ? Results.BadRequest(new { error = result.Error })
                    : Results.Conflict(new { error = result.Error });
            }

            return Results.Ok(await BuildPlayerResponseAsync(dbContext));
        });

        app.MapPost("/player/orders/{orderId:int}/cancel", async (int orderId, MarketService marketService) =>
        {
            var result = await marketService.CancelPlayerOrderAsync(orderId);
            return result is { Success: true, Order: not null }
                ? Results.Ok(ToOrderResponse(result.Order))
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapPost("/player/fund", async (OpenPlayerFundRequest request, MarketService marketService, AppDbContext dbContext) =>
        {
            var result = await marketService.OpenPlayerFundAsync(request.SeedAmount, request.Name);
            if (!result.Success)
            {
                // An existing managed fund is a conflict; everything else is a bad request.
                return result.Error == "The player already manages a fund."
                    ? Results.Conflict(new { error = result.Error })
                    : Results.BadRequest(new { error = result.Error });
            }

            return Results.Ok(await BuildPlayerResponseAsync(dbContext));
        });

        app.MapPost("/player/fund/deposit", async (PlayerFundCashRequest request, MarketService marketService, AppDbContext dbContext) =>
        {
            var result = await marketService.DepositToPlayerFundAsync(request.Amount);
            return result.Success
                ? Results.Ok(await BuildPlayerResponseAsync(dbContext))
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapPost("/player/fund/withdraw", async (PlayerFundCashRequest request, MarketService marketService, AppDbContext dbContext) =>
        {
            var result = await marketService.WithdrawFromPlayerFundAsync(request.Amount);
            return result.Success
                ? Results.Ok(await BuildPlayerResponseAsync(dbContext))
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapPost("/player/fund/close", async (MarketService marketService, AppDbContext dbContext) =>
        {
            var result = await marketService.ClosePlayerFundAsync();
            return result.Success
                ? Results.Ok(await BuildPlayerResponseAsync(dbContext))
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

        app.MapPost("/funds/{id:int}/advertise", async (int id, MarketService marketService, AppDbContext dbContext) =>
        {
            var result = await marketService.AdvertiseFundAsync(id);
            return result.Success
                ? Results.Ok(await BuildPlayerResponseAsync(dbContext))
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
                    snapshot.Capitalization,
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
            var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);

            var response = posts
                .Select(post => ToNewsResponse(post, companyNameById, industryNameById, cycleNumbersById))
                .ToArray();

            return Results.Ok(response);
        });

        // Server-paged news for the News page. News grows unbounded across cycles, so this pages in the
        // database rather than trimming a client-loaded list like the dashboard newswire does.
        app.MapGet("/news/paged", async (int? page, int? pageSize, AppDbContext dbContext) =>
        {
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var pageIndex = Math.Max(page ?? 1, 1);

            var total = await dbContext.NewsPosts.CountAsync();
            var posts = await dbContext.NewsPosts
                .OrderByDescending(post => post.Id)
                .Skip((pageIndex - 1) * size)
                .Take(size)
                .Include(post => post.Industries)
                .ToListAsync();

            var companyNameById = await dbContext.Companies
                .ToDictionaryAsync(company => company.Id, company => company.Name);
            var industryNameById = await IndustryNameByIdAsync(dbContext);
            var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);

            var items = posts
                .Select(post => ToNewsResponse(post, companyNameById, industryNameById, cycleNumbersById))
                .ToArray();

            return Results.Ok(new PagedNewsResponse(items, total, pageIndex, size));
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
            var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);

            return Results.Ok(ToNewsResponse(result.Post!, companyNameById, industryNameById, cycleNumbersById));
        });

        app.MapGet("/news/themes", (NewsImpactScope? scope) =>
        {
            var themes = scope is NewsImpactScope.Company or NewsImpactScope.Industries
                ? DemoNewsContent.ScopedThemeOptions
                : DemoNewsContent.ThemeOptions;
            return Results.Ok(themes
                .Select(theme => new NewsThemeResponse(theme.Key, theme.Label))
                .ToArray());
        });

        app.MapGet("/industries/sentiment-history", async (AppDbContext dbContext) =>
        {
            var industries = await dbContext.Industries
                .OrderBy(industry => industry.Name)
                .ToArrayAsync();
            var pointsByIndustry = (await IndustrySentimentHistoryRowsAsync(dbContext))
                .GroupBy(row => row.IndustryId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(ToIndustrySentimentPointResponse).ToArray());

            return Results.Ok(industries
                .Select(industry => new IndustrySentimentHistoryResponse(
                    industry.Id,
                    industry.Name,
                    pointsByIndustry.GetValueOrDefault(industry.Id, [])))
                .ToArray());
        });

        app.MapGet("/industries", async (AppDbContext dbContext) =>
        {
            var changesByIndustry = await LastSentimentChangeByIndustryAsync(dbContext);
            var industries = await dbContext.Industries
                .OrderBy(industry => industry.Name)
                .ToListAsync();

            return Results.Ok(industries
                .Select(industry => new IndustryResponse(
                    industry.Id,
                    industry.Name,
                    industry.SentimentValue,
                    industry.SentimentVolatility,
                    industry.SectorBeta,
                    changesByIndustry.GetValueOrDefault(industry.Id)))
                .ToArray());
        });

        app.MapGet("/industries/{industryId:int}/sentiment-history", async (int industryId, AppDbContext dbContext) =>
        {
            if (!await dbContext.Industries.AnyAsync(industry => industry.Id == industryId))
            {
                return Results.NotFound(new { error = "Industry not found." });
            }

            return Results.Ok((await IndustrySentimentHistoryRowsAsync(dbContext, industryId))
                .Select(ToIndustrySentimentPointResponse)
                .ToArray());
        });

        app.MapGet("/industries/{industryId:int}/news", async (int industryId, int? take, AppDbContext dbContext) =>
        {
            if (!await dbContext.Industries.AnyAsync(industry => industry.Id == industryId))
            {
                return Results.NotFound(new { error = "Industry not found." });
            }

            var limit = Math.Clamp(take ?? 20, 1, 100);
            var posts = await dbContext.NewsPosts
                .Where(post => post.Industries.Any(link => link.IndustryId == industryId))
                .OrderByDescending(post => post.Id)
                .Take(limit)
                .Include(post => post.Industries)
                .ToListAsync();
            var companyNameById = await dbContext.Companies
                .ToDictionaryAsync(company => company.Id, company => company.Name);
            var industryNameById = await IndustryNameByIdAsync(dbContext);
            var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);

            return Results.Ok(posts
                .Select(post => ToNewsResponse(post, companyNameById, industryNameById, cycleNumbersById))
                .ToArray());
        });

        app.MapGet("/industries/{industryId:int}", async (int industryId, AppDbContext dbContext) =>
        {
            var detail = await BuildIndustryDetailAsync(dbContext, industryId);
            return detail is null
                ? Results.NotFound(new { error = "Industry not found." })
                : Results.Ok(detail);
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
            var crisisIds = crises.Select(crisis => crisis.Id).ToList();
            var eventCountByCrisis = (await dbContext.CrisisEvents
                    .Where(crisisEvent => crisisIds.Contains(crisisEvent.CrisisId))
                    .GroupBy(crisisEvent => crisisEvent.CrisisId)
                    .Select(group => new { CrisisId = group.Key, Count = group.Count() })
                    .ToListAsync())
                .ToDictionary(entry => entry.CrisisId, entry => entry.Count);

            var response = crises
                .Select(crisis => ToCrisisResponse(crisis, industryNameById, eventCountByCrisis))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/crises/{id:int}", async (int id, AppDbContext dbContext) =>
        {
            var crisis = await dbContext.Crises
                .Where(crisis => crisis.Id == id)
                .Include(crisis => crisis.Industries)
                .Include(crisis => crisis.Events)
                .FirstOrDefaultAsync();

            if (crisis is null)
            {
                return Results.NotFound(new { error = "Crisis not found." });
            }

            var industryNameById = await IndustryNameByIdAsync(dbContext);
            var companyIds = crisis.Events
                .Where(crisisEvent => crisisEvent.CompanyId != null)
                .Select(crisisEvent => crisisEvent.CompanyId!.Value)
                .Distinct()
                .ToList();
            var companyNameById = await dbContext.Companies
                .Where(company => companyIds.Contains(company.Id))
                .ToDictionaryAsync(company => company.Id, company => company.Name);

            return Results.Ok(ToCrisisDetailResponse(crisis, industryNameById, companyNameById));
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

            // A bankrupt trader may since have left the market for good: its Participant row is deleted, so fall
            // back to the name archived on its MarketExit row before the numeric-id last resort in the mapper.
            var departedIds = participantIds.Where(id => !participantNameById.ContainsKey(id)).ToList();
            if (departedIds.Count > 0)
            {
                var departedNameById = await dbContext.MarketExits
                    .Where(marketExit => departedIds.Contains(marketExit.ParticipantId))
                    .ToDictionaryAsync(marketExit => marketExit.ParticipantId, marketExit => marketExit.Name);
                foreach (var departed in departedNameById)
                {
                    participantNameById[departed.Key] = departed.Value;
                }
            }

            var cycleNumberById = await dbContext.MarketCycles
                .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

            var response = bankruptcies
                .Select(bankruptcy => ToBankruptcyResponse(bankruptcy, participantNameById, cycleNumberById))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/market-exits", async (int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 30, 1, 200);
            var marketExits = await dbContext.MarketExits
                .OrderByDescending(marketExit => marketExit.Id)
                .Take(limit)
                .ToListAsync();

            var cycleNumberById = await dbContext.MarketCycles
                .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

            // The trader's name is denormalised onto the exit row (its Participant row is gone), so no join.
            var response = marketExits
                .Select(marketExit => ToMarketExitResponse(marketExit, cycleNumberById))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/collective-funds/closed", async (int? page, int? pageSize, AppDbContext dbContext) =>
        {
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var pageIndex = Math.Max(page ?? 1, 1);

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

        app.MapGet("/companies/closed", async (int? page, int? pageSize, AppDbContext dbContext) =>
        {
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var pageIndex = Math.Max(page ?? 1, 1);

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

        app.MapGet("/banks", async (AppDbContext dbContext) =>
        {
            var banks = await dbContext.Banks.OrderBy(bank => bank.Id).ToListAsync();
            var openByBank = (await dbContext.Loans
                    .Where(loan => loan.Status == LoanStatus.Open)
                    .Select(loan => new { loan.BankId, loan.RemainingPrincipal })
                    .ToListAsync())
                .GroupBy(loan => loan.BankId)
                .ToDictionary(
                    group => group.Key,
                    group => new { Count = group.Count(), Outstanding = group.Sum(loan => loan.RemainingPrincipal) });

            var items = banks
                .Select(bank => new BankResponse(
                    bank.Id,
                    bank.Name,
                    bank.InterestRatePerCycle,
                    bank.Balance,
                    openByBank.TryGetValue(bank.Id, out var open) ? open.Count : 0,
                    openByBank.TryGetValue(bank.Id, out var open2) ? open2.Outstanding : 0m))
                .ToArray();

            return Results.Ok(items);
        });

        app.MapGet("/loans/paged", async (
            int? page, int? pageSize, int? bankId, string? status, string? sort, string? sortDir,
            AppDbContext dbContext) =>
        {
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var pageIndex = Math.Max(page ?? 1, 1);
            var descending = !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);

            var query = dbContext.Loans.AsQueryable();
            if (bankId is int bank)
            {
                query = query.Where(loan => loan.BankId == bank);
            }

            query = FilterLoansByStatus(query, status);
            var total = await query.CountAsync();

            IOrderedQueryable<Loan> ordered = sort switch
            {
                "principal" => descending ? query.OrderByDescending(loan => loan.RemainingPrincipal) : query.OrderBy(loan => loan.RemainingPrincipal),
                "pastDue" => descending ? query.OrderByDescending(loan => loan.PastDueAmount) : query.OrderBy(loan => loan.PastDueAmount),
                "term" => descending ? query.OrderByDescending(loan => loan.TermCycles) : query.OrderBy(loan => loan.TermCycles),
                _ => descending ? query.OrderByDescending(loan => loan.Id) : query.OrderBy(loan => loan.Id),
            };

            var loans = await ordered.Skip((pageIndex - 1) * size).Take(size).ToListAsync();
            var items = (await BuildLoanResponsesAsync(dbContext, loans)).ToArray();
            return Results.Ok(new PagedLoansResponse(items, total, pageIndex, size));
        });

        app.MapGet("/participants/{participantId:int}/loans", async (int participantId, string? status, AppDbContext dbContext) =>
        {
            var query = dbContext.Loans.Where(loan => loan.ParticipantId == participantId);
            query = string.Equals(status, "all", StringComparison.OrdinalIgnoreCase)
                ? query
                : query.Where(loan => loan.Status == LoanStatus.Open);

            var loans = await query.OrderByDescending(loan => loan.Id).ToListAsync();
            return Results.Ok((await BuildLoanResponsesAsync(dbContext, loans)).ToArray());
        });

        app.MapPost("/loans/{loanId:int}/repay", async (int loanId, RepayLoanRequest? request, MarketService marketService, AppDbContext dbContext) =>
        {
            var result = await marketService.RepayLoanAsync(loanId, request?.Amount);
            if (!result.Success || result.Loan is null)
            {
                return Results.BadRequest(new { error = result.Error });
            }

            var response = (await BuildLoanResponsesAsync(dbContext, [result.Loan])).Single();
            return Results.Ok(response);
        });
    }

    private static IQueryable<Loan> FilterLoansByStatus(IQueryable<Loan> query, string? status) =>
        status?.ToLowerInvariant() switch
        {
            "closed" => query.Where(loan => loan.Status == LoanStatus.Closed),
            "all" => query,
            _ => query.Where(loan => loan.Status == LoanStatus.Open),
        };

    private static async Task<List<LoanResponse>> BuildLoanResponsesAsync(AppDbContext dbContext, IReadOnlyList<Loan> loans)
    {
        if (loans.Count == 0)
        {
            return [];
        }

        var bankIds = loans.Select(loan => loan.BankId).Distinct().ToList();
        var bankNames = await dbContext.Banks
            .Where(bank => bankIds.Contains(bank.Id))
            .ToDictionaryAsync(bank => bank.Id, bank => bank.Name);

        var participantIds = loans.Select(loan => loan.ParticipantId).Distinct().ToList();
        var participantNames = await LoanParticipantNamesAsync(dbContext, participantIds);

        var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);
        var currentCycleNumber = await CurrentCycleNumberAsync(dbContext);

        return loans
            .Select(loan => ToLoanResponse(loan, bankNames, participantNames, cycleNumbersById, currentCycleNumber))
            .ToList();
    }

    private static LoanResponse ToLoanResponse(
        Loan loan,
        IReadOnlyDictionary<int, string> bankNames,
        IReadOnlyDictionary<int, string> participantNames,
        IReadOnlyDictionary<int, int> cycleNumbersById,
        int currentCycleNumber)
    {
        var openedNumber = cycleNumbersById.GetValueOrDefault(loan.OpenedInCycleId);
        var dueNumber = openedNumber + loan.TermCycles;
        var interestPerCycle = Math.Round(loan.RemainingPrincipal * loan.InterestRatePerCycle, 2, MidpointRounding.AwayFromZero);

        return new LoanResponse(
            loan.Id,
            loan.BankId,
            bankNames.GetValueOrDefault(loan.BankId, $"#{loan.BankId}"),
            loan.ParticipantId,
            participantNames.GetValueOrDefault(loan.ParticipantId, $"#{loan.ParticipantId}"),
            loan.Principal,
            loan.RemainingPrincipal,
            loan.InterestRatePerCycle,
            interestPerCycle,
            loan.ScheduledInstallment,
            loan.PastDueAmount,
            loan.RemainingPrincipal + loan.PastDueAmount,
            loan.TermCycles,
            openedNumber,
            dueNumber,
            Math.Max(0, dueNumber - currentCycleNumber),
            loan.Status.ToString(),
            loan.ClosedInCycleId is int closedCycleId ? cycleNumbersById.GetValueOrDefault(closedCycleId) : null,
            loan.Status == LoanStatus.Closed,
            loan.CloseReason?.ToString());
    }

    // Loan borrowers are live participants first, with a MarketExit fallback so a departed borrower's closed
    // loans still carry a name.
    private static async Task<Dictionary<int, string>> LoanParticipantNamesAsync(AppDbContext dbContext, IReadOnlyList<int> participantIds)
    {
        var names = await ParticipantNamesAsync(dbContext, participantIds);
        var missing = participantIds.Where(id => !names.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var exitNames = await dbContext.MarketExits
                .Where(exit => missing.Contains(exit.ParticipantId))
                .Select(exit => new { exit.ParticipantId, exit.Name })
                .ToListAsync();
            foreach (var exit in exitNames)
            {
                names.TryAdd(exit.ParticipantId, exit.Name);
            }
        }

        return names;
    }

    private static async Task<Dictionary<int, string>> IndustryNameByIdAsync(AppDbContext dbContext) =>
        await dbContext.Industries.ToDictionaryAsync(industry => industry.Id, industry => industry.Name);

    private static async Task<List<CompanyResponse>> BuildCompanyResponsesAsync(AppDbContext dbContext)
    {
        // Delisted companies drop off the live roster and the dashboard map; they surface on the closed-companies
        // page and still resolve on their own detail route.
        var companies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .OrderBy(company => company.Id)
            .ToListAsync();
        var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);
        var changeByCompany = await PriceChangePctByCompanyAsync(dbContext);
        var industryNameById = await IndustryNameByIdAsync(dbContext);
        var latestRatingByCompany = await LatestRatingByCompanyAsync(dbContext);
        var currentCycleNumber = await CurrentCycleNumberAsync(dbContext);

        return companies
            .Select(company => new CompanyResponse(
                company.Id,
                company.Name,
                company.IndustryId,
                industryNameById.GetValueOrDefault(company.IndustryId),
                company.IssuedSharesCount,
                latestPriceByCompany.GetValueOrDefault(company.Id),
                changeByCompany.GetValueOrDefault(company.Id),
                latestRatingByCompany.TryGetValue(company.Id, out var rating) ? rating.ToString() : null,
                company.TradingHaltedUntilCycleNumber is int haltedUntil && haltedUntil >= currentCycleNumber))
            .ToList();
    }

    private static async Task<List<ParticipantResponse>> BuildParticipantResponsesAsync(AppDbContext dbContext)
    {
        var participants = await dbContext.Participants.OrderBy(participant => participant.Id).ToListAsync();

        // A trader that has pooled into a fund hands its trading to that fund, so it is dropped from the
        // traders list while the membership lasts; it returns once it leaves or the fund closes.
        var memberParticipantIds = (await dbContext.CollectiveFundParticipants
                .Select(member => member.ParticipantId)
                .ToListAsync())
            .ToHashSet();

        var holdingsByOwner = (await dbContext.Holdings
                .Where(holding => holding.Quantity > 0)
                .Select(holding => new { OwnerId = holding.ParticipantId, holding.CompanyId, Count = holding.Quantity })
                .ToListAsync())
            .GroupBy(entry => entry.OwnerId)
            .ToList();

        var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);

        var sharesOwnedByParticipant = holdingsByOwner.ToDictionary(
            group => group.Key,
            group => group.Sum(holding => holding.Count));

        // Distinct companies a trader holds shares in — one group per company after the owner+company grouping.
        var companiesOwnedByParticipant = holdingsByOwner.ToDictionary(
            group => group.Key,
            group => group.Count());

        // Estimated market value of a trader's shares: each holding valued at its company's latest price.
        var holdingsValueByParticipant = holdingsByOwner.ToDictionary(
            group => group.Key,
            group => group.Sum(holding => holding.Count * latestPriceByCompany.GetValueOrDefault(holding.CompanyId)));

        var loanLiabilityByParticipant = await LoanService.OpenLoanLiabilityByParticipantAsync(dbContext);

        return participants
            .Where(participant => !memberParticipantIds.Contains(participant.Id))
            // A closed fund lives on as an inactive, zeroed-out row; keep it off the live roster and surface it
            // on the Closed Funds page instead. Only funds are dropped here — bankrupt traders stay listed.
            .Where(participant => participant.Type != ParticipantType.CollectiveFund || participant.IsActive)
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
                companiesOwnedByParticipant.GetValueOrDefault(participant.Id),
                holdingsValueByParticipant.GetValueOrDefault(participant.Id),
                loanLiabilityByParticipant.GetValueOrDefault(participant.Id),
                participant.IsActive,
                participant.IsBankrupt))
            .ToList();
    }

    // The current risk rating per company is its most recent verdict, found by max rating Id so the whole
    // rating history never has to be loaded.
    private static async Task<Dictionary<int, CompanyRiskRating>> LatestRatingByCompanyAsync(AppDbContext dbContext)
    {
        var latestRatingIds = await dbContext.CompanyRatings
            .GroupBy(rating => rating.CompanyId)
            .Select(group => group.Max(rating => rating.Id))
            .ToListAsync();

        return (await dbContext.CompanyRatings
                .Where(rating => latestRatingIds.Contains(rating.Id))
                .Select(rating => new { rating.CompanyId, rating.Rating })
                .ToListAsync())
            .ToDictionary(row => row.CompanyId, row => row.Rating);
    }

    private static async Task<Dictionary<int, int>> LastSentimentChangeByIndustryAsync(AppDbContext dbContext)
    {
        var snapshots = await dbContext.SectorSentimentSnapshots
            .Select(snapshot => new { snapshot.Id, snapshot.IndustryId, snapshot.SentimentValue })
            .ToListAsync();

        return snapshots
            .GroupBy(snapshot => snapshot.IndustryId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var recent = group.OrderByDescending(snapshot => snapshot.Id).Take(2).ToArray();
                    return recent.Length == 2 ? recent[0].SentimentValue - recent[1].SentimentValue : 0;
                });
    }

    private static async Task<IndustrySentimentHistoryRow[]> IndustrySentimentHistoryRowsAsync(
        AppDbContext dbContext,
        int? industryId = null)
    {
        var query =
            from snapshot in dbContext.SectorSentimentSnapshots
            join cycle in dbContext.MarketCycles on snapshot.CreatedInCycleId equals cycle.Id
            where industryId == null || snapshot.IndustryId == industryId
            orderby cycle.CycleNumber, snapshot.Id
            select new IndustrySentimentHistoryRow(
                snapshot.IndustryId,
                snapshot.CreatedInCycleId,
                cycle.CycleNumber,
                snapshot.SentimentValue,
                snapshot.CreatedAt);

        return await query.ToArrayAsync();
    }

    private static IndustrySentimentPointResponse ToIndustrySentimentPointResponse(IndustrySentimentHistoryRow row) =>
        new(row.CreatedInCycleId, row.CycleNumber, row.SentimentValue, row.CreatedAt);

    private static NewsPostResponse ToNewsResponse(
        NewsPost post,
        IReadOnlyDictionary<int, string> companyNameById,
        IReadOnlyDictionary<int, string> industryNameById,
        IReadOnlyDictionary<int, int> cycleNumbersById) =>
        new(
            post.Id,
            post.Title,
            post.Content,
            post.PublishedInCycleId,
            cycleNumbersById.GetValueOrDefault(post.PublishedInCycleId),
            post.PublishedAt,
            post.Scope.ToString(),
            post.Category.ToString(),
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
        IReadOnlyDictionary<int, int> eventCountByCrisis) =>
        new(
            crisis.Id,
            crisis.Title,
            crisis.Content,
            crisis.Scope.ToString(),
            crisis.TriggeredInCycleId,
            crisis.TriggeredInCycleNumber,
            crisis.DurationCycles,
            crisis.TriggeredAt,
            eventCountByCrisis.GetValueOrDefault(crisis.Id),
            ToCrisisIndustryResponses(crisis, industryNameById));

    private static CrisisIndustryResponse[] ToCrisisIndustryResponses(
        Crisis crisis,
        IReadOnlyDictionary<int, string> industryNameById) =>
        crisis.Industries
            .Select(link => new CrisisIndustryResponse(
                link.IndustryId,
                industryNameById.GetValueOrDefault(link.IndustryId) ?? $"#{link.IndustryId}",
                link.ImpactPercent))
            .ToArray();

    private static CrisisDetailResponse ToCrisisDetailResponse(
        Crisis crisis,
        IReadOnlyDictionary<int, string> industryNameById,
        IReadOnlyDictionary<int, string> companyNameById) =>
        new(
            crisis.Id,
            crisis.Title,
            crisis.Content,
            crisis.Scope.ToString(),
            crisis.TriggeredInCycleId,
            crisis.TriggeredInCycleNumber,
            crisis.DurationCycles,
            crisis.TriggeredAt,
            ToCrisisIndustryResponses(crisis, industryNameById),
            crisis.Events
                .OrderBy(crisisEvent => crisisEvent.CreatedInCycleNumber)
                .ThenBy(crisisEvent => crisisEvent.Id)
                .Select(crisisEvent => new CrisisEventResponse(
                    crisisEvent.Id,
                    crisisEvent.Type.ToString(),
                    crisisEvent.Description,
                    crisisEvent.CompanyId,
                    crisisEvent.CompanyId is int companyId
                        ? companyNameById.GetValueOrDefault(companyId)
                        : null,
                    crisisEvent.IndustryId,
                    crisisEvent.IndustryId is int industryId
                        ? industryNameById.GetValueOrDefault(industryId)
                        : null,
                    crisisEvent.ImpactPercent,
                    crisisEvent.CreatedInCycleNumber,
                    crisisEvent.CreatedAt))
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

    private static MarketExitResponse ToMarketExitResponse(
        MarketExit marketExit,
        IReadOnlyDictionary<int, int> cycleNumberById) =>
        new(
            marketExit.Id,
            marketExit.ParticipantId,
            marketExit.Name,
            marketExit.Reason,
            cycleNumberById.GetValueOrDefault(marketExit.JoinedInCycleId),
            cycleNumberById.GetValueOrDefault(marketExit.LeftInCycleId),
            marketExit.OrdersPlaced,
            marketExit.InitialBalance,
            marketExit.MaxTotalWorth,
            marketExit.QuitBalance,
            marketExit.LeftAt);

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

    private static async Task<IndustryDetailResponse?> BuildIndustryDetailAsync(AppDbContext dbContext, int industryId)
    {
        var industry = await dbContext.Industries.FirstOrDefaultAsync(candidate => candidate.Id == industryId);
        if (industry is null)
        {
            return null;
        }

        var companies = await dbContext.Companies
            .Where(company => company.IndustryId == industryId && company.ClosedInCycleId == null)
            .Select(company => new { company.Id, company.IssuedSharesCount })
            .ToListAsync();
        var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);
        var lastCycleChange = (await LastSentimentChangeByIndustryAsync(dbContext)).GetValueOrDefault(industryId);

        return new IndustryDetailResponse(
            industry.Id,
            industry.Name,
            industry.SentimentValue,
            industry.SentimentVolatility,
            industry.SectorBeta,
            companies.Sum(company => company.IssuedSharesCount * latestPriceByCompany.GetValueOrDefault(company.Id)),
            lastCycleChange,
            companies.Count);
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

    private static Task<Dictionary<int, int>> CycleNumbersByIdAsync(AppDbContext dbContext) =>
        dbContext.MarketCycles.ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

    // The number of the market's active cycle, or zero when no market or cycle is set.
    private static async Task<int> CurrentCycleNumberAsync(AppDbContext dbContext)
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market?.CurrentCycleId is not int cycleId)
        {
            return 0;
        }

        return await dbContext.MarketCycles
            .Where(cycle => cycle.Id == cycleId)
            .Select(cycle => cycle.CycleNumber)
            .FirstOrDefaultAsync();
    }

    private static async Task<CompanyDetailResponse?> BuildCompanyDetailAsync(AppDbContext dbContext, int companyId)
    {
        var company = await dbContext.Companies.FirstOrDefaultAsync(candidate => candidate.Id == companyId);
        if (company is null)
        {
            return null;
        }

        // Outstanding shares are the sum of participants' holdings; the rest of the issued supply is the
        // unsold float still held by the issuer.
        var sharesOutstanding = await dbContext.Holdings
            .Where(holding => holding.CompanyId == companyId && holding.Quantity > 0)
            .SumAsync(holding => holding.Quantity);
        var sharesHeldByIssuer = company.IssuedSharesCount - sharesOutstanding;
        var shareholderCount = await dbContext.Holdings
            .CountAsync(holding => holding.CompanyId == companyId && holding.Quantity > 0);

        var currentPrice = (await LatestPriceByCompanyAsync(dbContext)).GetValueOrDefault(companyId);
        var priceChangePct = (await PriceChangePctByCompanyAsync(dbContext)).GetValueOrDefault(companyId);
        var industryName = await dbContext.Industries
            .Where(industry => industry.Id == company.IndustryId)
            .Select(industry => industry.Name)
            .FirstOrDefaultAsync();

        // The two most recent verdicts give the current risk rating and the direction of its change.
        var recentRatings = await dbContext.CompanyRatings
            .Where(rating => rating.CompanyId == companyId)
            .OrderByDescending(rating => rating.Id)
            .Take(2)
            .Select(rating => rating.Rating)
            .ToListAsync();

        int? closedInCycleNumber = company.ClosedInCycleId is int closedCycleId
            ? await dbContext.MarketCycles
                .Where(cycle => cycle.Id == closedCycleId)
                .Select(cycle => (int?)cycle.CycleNumber)
                .FirstOrDefaultAsync()
            : null;

        var currentCycleNumber = await CurrentCycleNumberAsync(dbContext);
        var isHalted = company.TradingHaltedUntilCycleNumber is int haltedUntil && haltedUntil >= currentCycleNumber;

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
            company.CreatedAt,
            recentRatings.Count > 0 ? recentRatings[0].ToString() : null,
            recentRatings.Count > 1 ? recentRatings[1].ToString() : null,
            company.ClosedInCycleId != null,
            closedInCycleNumber,
            isHalted,
            isHalted ? company.TradingHaltedUntilCycleNumber : null);
    }

    private static async Task<ParticipantDetailResponse?> BuildParticipantDetailAsync(AppDbContext dbContext, int participantId)
    {
        var participant = await dbContext.Participants.FirstOrDefaultAsync(candidate => candidate.Id == participantId);
        if (participant is null)
        {
            return null;
        }

        var sharesOwned = await dbContext.Holdings
            .Where(holding => holding.ParticipantId == participantId && holding.Quantity > 0)
            .SumAsync(holding => holding.Quantity);

        var loanLiability = await dbContext.Loans
            .Where(loan => loan.ParticipantId == participantId && loan.Status == LoanStatus.Open)
            .SumAsync(loan => loan.RemainingPrincipal + loan.PastDueAmount);

        string? fundStatus = null;
        CollectiveFundMemberResponse[] fundMembers = [];
        if (participant.Type == ParticipantType.CollectiveFund)
        {
            (fundStatus, fundMembers) = await BuildCollectiveFundMembersAsync(dbContext, participantId);
        }

        // For an ordinary trader, surface the fund it has joined (if any) so its page can link there.
        int? memberOfFundId = null;
        string? memberOfFundName = null;
        var membership = await dbContext.CollectiveFundParticipants
            .FirstOrDefaultAsync(member => member.ParticipantId == participantId);
        if (membership is not null)
        {
            var fund = await dbContext.CollectiveFunds.FirstOrDefaultAsync(candidate => candidate.Id == membership.CollectiveFundId);
            if (fund is not null)
            {
                memberOfFundId = fund.ParticipantId;
                memberOfFundName = await dbContext.Participants
                    .Where(candidate => candidate.Id == fund.ParticipantId)
                    .Select(candidate => candidate.Name)
                    .FirstOrDefaultAsync();
            }
        }

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
            loanLiability,
            participant.IsActive,
            fundStatus,
            fundMembers,
            memberOfFundId,
            memberOfFundName);
    }

    private static async Task<PlayerResponse?> BuildPlayerResponseAsync(AppDbContext dbContext)
    {
        var player = await dbContext.Participants
            .FirstOrDefaultAsync(participant => participant.Type == ParticipantType.Player);
        if (player is null)
        {
            return null;
        }

        var holdings = await dbContext.Holdings
            .Where(holding => holding.ParticipantId == player.Id && holding.Quantity > 0)
            .Select(holding => new { holding.CompanyId, Shares = holding.Quantity })
            .ToListAsync();

        var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);
        var sharesOwned = holdings.Sum(holding => holding.Shares);
        var holdingsValue = holdings.Sum(holding =>
            holding.Shares * latestPriceByCompany.GetValueOrDefault(holding.CompanyId));

        var loanLiability = await dbContext.Loans
            .Where(loan => loan.ParticipantId == player.Id && loan.Status == LoanStatus.Open)
            .SumAsync(loan => loan.RemainingPrincipal + loan.PastDueAmount);

        // Net worth subtracts loan debt; gross holdings value is reported separately.
        var totalWorth = player.CurrentBalance + holdingsValue - loanLiability;

        // The two newest snapshots are cycles N and N−1; last-cycle deltas stay null until both exist.
        var recentSnapshots = await dbContext.ParticipantWorthSnapshots
            .Where(snapshot => snapshot.ParticipantId == player.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .Take(2)
            .ToListAsync();

        decimal? lastCycleMoneyChange = null;
        decimal? lastCycleWorthChange = null;
        if (recentSnapshots.Count == 2)
        {
            var latest = recentSnapshots[0];
            var prior = recentSnapshots[1];
            lastCycleMoneyChange = latest.Balance - prior.Balance;
            lastCycleWorthChange = latest.Balance + latest.HoldingsValue - latest.LoanLiability
                - (prior.Balance + prior.HoldingsValue - prior.LoanLiability);
        }

        // The player-managed fund (if any) rides along on the player response so the UI can trade through it and
        // manage its cash without a second round-trip.
        var managedFund = await dbContext.CollectiveFunds
            .FirstOrDefaultAsync(fund => fund.IsPlayerManaged
                && fund.FoundedByParticipantId == player.Id
                && fund.Status != CollectiveFundStatus.Closed);

        int? fundParticipantId = null;
        string? fundName = null;
        decimal? fundCurrentBalance = null;
        decimal? fundAvailableBalance = null;
        decimal? fundHoldingsValue = null;
        decimal? fundTotalWorth = null;
        decimal? fundWithdrawable = null;
        int? fundPopularityIndex = null;
        if (managedFund is not null)
        {
            fundPopularityIndex = managedFund.PopularityIndex;
            var fundParticipant = await dbContext.Participants
                .FirstOrDefaultAsync(participant => participant.Id == managedFund.ParticipantId);
            if (fundParticipant is not null)
            {
                var fundHoldings = await dbContext.Holdings
                    .Where(holding => holding.ParticipantId == fundParticipant.Id && holding.Quantity > 0)
                    .Select(holding => new { holding.CompanyId, Shares = holding.Quantity })
                    .ToListAsync();
                var fundHoldingsVal = fundHoldings.Sum(holding =>
                    holding.Shares * latestPriceByCompany.GetValueOrDefault(holding.CompanyId));
                var memberDepositsOwed = await dbContext.CollectiveFundParticipants
                    .Where(member => member.CollectiveFundId == managedFund.Id)
                    .SumAsync(member => member.DepositAmount);

                fundParticipantId = fundParticipant.Id;
                fundName = fundParticipant.Name;
                fundCurrentBalance = fundParticipant.CurrentBalance;
                fundAvailableBalance = fundParticipant.AvailableBalance;
                fundHoldingsValue = fundHoldingsVal;
                fundTotalWorth = fundParticipant.CurrentBalance + fundHoldingsVal;
                fundWithdrawable = Math.Max(0m, fundParticipant.AvailableBalance - memberDepositsOwed);
            }
        }

        return new PlayerResponse(
            player.Id,
            player.Name,
            player.InitialBalance,
            player.CurrentBalance,
            player.ReservedBalance,
            player.AvailableBalance,
            sharesOwned,
            holdingsValue,
            loanLiability,
            totalWorth,
            player.CurrentBalance - player.InitialBalance,
            totalWorth - player.InitialBalance,
            lastCycleMoneyChange,
            lastCycleWorthChange,
            player.IsActive,
            fundParticipantId,
            fundName,
            fundCurrentBalance,
            fundAvailableBalance,
            fundHoldingsValue,
            fundTotalWorth,
            fundWithdrawable,
            fundPopularityIndex);
    }

    private static async Task<(string? Status, CollectiveFundMemberResponse[] Members)> BuildCollectiveFundMembersAsync(
        AppDbContext dbContext,
        int fundParticipantId)
    {
        var fund = await dbContext.CollectiveFunds.FirstOrDefaultAsync(candidate => candidate.ParticipantId == fundParticipantId);
        if (fund is null)
        {
            return (null, []);
        }

        var memberships = await dbContext.CollectiveFundParticipants
            .Where(member => member.CollectiveFundId == fund.Id)
            .ToListAsync();
        var memberIds = memberships.Select(member => member.ParticipantId).ToList();
        var memberById = await dbContext.Participants
            .Where(candidate => memberIds.Contains(candidate.Id))
            .ToDictionaryAsync(candidate => candidate.Id);
        var cycleNumberById = await dbContext.MarketCycles
            .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

        // What the fund has paid each member as pass-through dividends, kept separate from their own holdings' dividends.
        var payoutByMember = (await dbContext.MoneyTransactions
                .Where(transaction => memberIds.Contains(transaction.ParticipantId)
                    && transaction.Type == MoneyTransactionType.CollectiveFundDividend)
                .GroupBy(transaction => transaction.ParticipantId)
                .Select(group => new { ParticipantId = group.Key, Total = group.Sum(transaction => transaction.Amount) })
                .ToListAsync())
            .ToDictionary(entry => entry.ParticipantId, entry => entry.Total);

        var members = memberships
            .OrderBy(member => member.JoinedAt)
            .Select(member =>
            {
                var memberParticipant = memberById.GetValueOrDefault(member.ParticipantId);
                return new CollectiveFundMemberResponse(
                    member.ParticipantId,
                    memberParticipant?.Name ?? $"#{member.ParticipantId}",
                    (memberParticipant?.Type ?? ParticipantType.Individual).ToString(),
                    cycleNumberById.GetValueOrDefault(member.JoinedInCycleId),
                    member.JoinedAt,
                    member.DepositAmount,
                    payoutByMember.GetValueOrDefault(member.ParticipantId),
                    member.IsLeaving);
            })
            .ToArray();

        return (fund.Status.ToString(), members);
    }

    private static async Task<Dictionary<int, string>> ParticipantNamesAsync(
        AppDbContext dbContext, IEnumerable<int> participantIds)
    {
        var ids = participantIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        return await dbContext.Participants
            .Where(participant => ids.Contains(participant.Id))
            .ToDictionaryAsync(participant => participant.Id, participant => participant.Name);
    }

    // The prevailing market price immediately before each trade — the latest price snapshot strictly older than
    // the trade's own snapshot — so a fill can be shown against the market it hit rather than against its own print
    // (a trade and the snapshot it stamps share the same price). Returns transaction id → that earlier price.
    private static async Task<Dictionary<int, decimal>> MarketPriceBeforeTradesAsync(
        AppDbContext dbContext, int companyId, IReadOnlyList<ShareTransaction> transactions)
    {
        var result = new Dictionary<int, decimal>();
        if (transactions.Count == 0)
        {
            return result;
        }

        var transactionIds = transactions.Select(transaction => transaction.Id).ToList();
        var tradeSnapshots = await dbContext.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == companyId
                && snapshot.SourceShareTransactionId != null
                && transactionIds.Contains(snapshot.SourceShareTransactionId.Value))
            .Select(snapshot => new { snapshot.Id, TransactionId = snapshot.SourceShareTransactionId!.Value })
            .ToListAsync();
        if (tradeSnapshots.Count == 0)
        {
            return result;
        }

        var minSnapshotId = tradeSnapshots.Min(snapshot => snapshot.Id);
        var maxSnapshotId = tradeSnapshots.Max(snapshot => snapshot.Id);

        // The snapshots in the id window spanning these trades, plus the one right before the oldest, are enough
        // to resolve every trade's preceding price by scanning in ascending id order.
        var window = (await dbContext.PriceSnapshots
                .Where(snapshot => snapshot.CompanyId == companyId
                    && snapshot.Id >= minSnapshotId && snapshot.Id <= maxSnapshotId)
                .OrderBy(snapshot => snapshot.Id)
                .Select(snapshot => new { snapshot.Id, snapshot.Price })
                .ToListAsync())
            .Select(snapshot => (snapshot.Id, snapshot.Price))
            .ToList();

        var beforeOldest = await dbContext.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == companyId && snapshot.Id < minSnapshotId)
            .OrderByDescending(snapshot => snapshot.Id)
            .Select(snapshot => new { snapshot.Id, snapshot.Price })
            .FirstOrDefaultAsync();
        if (beforeOldest is not null)
        {
            window.Insert(0, (beforeOldest.Id, beforeOldest.Price));
        }

        foreach (var trade in tradeSnapshots)
        {
            decimal? before = null;
            foreach (var (id, price) in window)
            {
                if (id >= trade.Id)
                {
                    break;
                }

                before = price;
            }

            if (before is decimal earlier)
            {
                result[trade.TransactionId] = earlier;
            }
        }

        return result;
    }

    // Names and the pre-trade market price are left null here and filled by the callers that hold a lookup,
    // so this mapper stays a single-argument method group usable by every other endpoint.
    private static ShareTransactionResponse ToShareTransactionResponse(ShareTransaction transaction) => new(
        transaction.Id,
        transaction.SellerId,
        null,
        transaction.BuyerId,
        null,
        transaction.CompanyId,
        transaction.Quantity,
        transaction.Price,
        null,
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
        null,
        order.CompanyId,
        order.Type.ToString(),
        order.Status.ToString(),
        order.Quantity,
        order.FilledQuantity,
        order.LimitPrice,
        order.ReservedCashAmount,
        order.CreatedInCycleId);

    private sealed record IndustrySentimentHistoryRow(
        int IndustryId,
        int CreatedInCycleId,
        int CycleNumber,
        int SentimentValue,
        DateTime CreatedAt);
}

public sealed record CompanyResponse(
    int Id,
    string Name,
    int IndustryId,
    string? IndustryName,
    int IssuedSharesCount,
    decimal? CurrentPrice,
    decimal PriceChangePct,
    string? CurrentRating,
    bool IsHalted);

public sealed record CompanyAttentionResponse(
    int CompanyId,
    string Name,
    string? IndustryName,
    decimal? CurrentPrice,
    decimal PriceChangePct,
    string? CurrentRating,
    int Shares,
    decimal MarketValue,
    bool PriceDeclining,
    bool BadNewsImpact,
    bool HighRisk,
    bool RecentMerge);

public sealed record PagedCompaniesResponse(CompanyResponse[] Items, int Total, int Page, int PageSize);

public sealed record PagedParticipantsResponse(ParticipantResponse[] Items, int Total, int Page, int PageSize);

public sealed record PagedNewsResponse(NewsPostResponse[] Items, int Total, int Page, int PageSize);

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
    DateTime CreatedAt,
    string? CurrentRating,
    string? PreviousRating,
    bool IsClosed,
    int? ClosedInCycleNumber,
    bool IsHalted,
    int? HaltedUntilCycleNumber);

public sealed record ShareholderResponse(
    int OwnerId,
    string OwnerName,
    int Shares,
    decimal MarketValue,
    decimal CostBasis,
    decimal PctOfIssued);

public sealed record AuditorResponse(int Id, string Name, string Description, int AuditCount);

public sealed record AuditRowResponse(
    int Id,
    int CompanyId,
    string CompanyName,
    string Rating,
    decimal? ImpactPercent,
    int CyclesAgo,
    DateTime CreatedAt);

public sealed record PagedAuditsResponse(AuditRowResponse[] Items, int Total, int Page, int PageSize);

public sealed record ClosedFundResponse(
    int Id,
    int ParticipantId,
    string Name,
    string? Temperament,
    string? RiskProfile,
    decimal PeakNetWorth,
    int CreatedInCycleNumber,
    DateTime? ClosedAt);

public sealed record PagedClosedFundsResponse(ClosedFundResponse[] Items, int Total, int Page, int PageSize);

public sealed record FundMembershipEventResponse(
    int Id,
    string Type,
    decimal Amount,
    int CollectiveFundId,
    int MemberParticipantId,
    string MemberName,
    int FundParticipantId,
    string FundName,
    int CreatedInCycleId,
    int CreatedInCycleNumber,
    DateTime CreatedAt);

public sealed record PagedFundMembershipEventsResponse(FundMembershipEventResponse[] Items, int Total, int Page, int PageSize);

public sealed record ClosedCompanyResponse(
    int Id,
    string Name,
    int IndustryId,
    string? IndustryName,
    int IssuedSharesCount,
    decimal? FinalPrice,
    int CreatedInCycleNumber,
    int ClosedInCycleNumber,
    DateTime? ClosedAt);

public sealed record PagedClosedCompaniesResponse(ClosedCompanyResponse[] Items, int Total, int Page, int PageSize);

public sealed record BankResponse(
    int Id,
    string Name,
    decimal InterestRatePerCycle,
    decimal Balance,
    int OpenLoanCount,
    decimal OutstandingPrincipal);

public sealed record LoanResponse(
    int Id,
    int BankId,
    string BankName,
    int ParticipantId,
    string ParticipantName,
    decimal Principal,
    decimal RemainingPrincipal,
    decimal InterestRatePerCycle,
    decimal InterestPerCycleAmount,
    decimal ScheduledInstallment,
    decimal PastDueAmount,
    decimal TotalLiability,
    int TermCycles,
    int OpenedInCycleNumber,
    int DueInCycleNumber,
    int RemainingTermCycles,
    string Status,
    int? ClosedInCycleNumber,
    bool IsClosed,
    string? CloseReason);

public sealed record PagedLoansResponse(LoanResponse[] Items, int Total, int Page, int PageSize);

public sealed record RepayLoanRequest(decimal? Amount);

public sealed record CompanyRatingResponse(
    int Id,
    string Rating,
    decimal? ImpactPercent,
    string AuditorName,
    int CyclesAgo,
    DateTime CreatedAt);

public sealed record ShareEmissionResponse(
    int Id,
    int SharesEmitted,
    int RecipientCount,
    int CyclesAgo,
    DateTime CreatedAt);

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
    int CompaniesOwned,
    decimal HoldingsValue,
    decimal LoanLiability,
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
    decimal LoanLiability,
    bool IsActive,
    string? CollectiveFundStatus,
    CollectiveFundMemberResponse[] CollectiveFundMembers,
    int? MemberOfCollectiveFundId,
    string? MemberOfCollectiveFundName);

public sealed record CollectiveFundMemberResponse(
    int ParticipantId,
    string Name,
    string Type,
    int JoinedInCycleNumber,
    DateTime JoinedAt,
    decimal Deposit,
    decimal Payouts,
    bool IsLeaving);

public sealed record UpdateParticipantProfileRequest(Temperament Temperament, RiskProfile RiskProfile);

public sealed record CreatePlayerRequest(string? Name);

public sealed record OpenPlayerFundRequest(decimal SeedAmount, string? Name);

public sealed record PlayerFundCashRequest(decimal Amount);

public sealed record FundAdvertiseQuoteResponse(
    decimal Price,
    decimal Fraction,
    decimal GrowthPct,
    decimal FundWorth,
    int PopularityIndex);

public sealed record PlayerResponse(
    int Id,
    string Name,
    decimal InitialBalance,
    decimal CurrentBalance,
    decimal ReservedBalance,
    decimal AvailableBalance,
    int SharesOwned,
    decimal HoldingsValue,
    decimal LoanLiability,
    decimal TotalWorth,
    decimal OverallMoneyChange,
    decimal OverallWorthChange,
    decimal? LastCycleMoneyChange,
    decimal? LastCycleWorthChange,
    bool IsActive,
    int? FundParticipantId,
    string? FundName,
    decimal? FundCurrentBalance,
    decimal? FundAvailableBalance,
    decimal? FundHoldingsValue,
    decimal? FundTotalWorth,
    decimal? FundWithdrawable,
    int? FundPopularityIndex);

public sealed record PlaceOrderRequest(int ParticipantId, int CompanyId, OrderType Type, int Quantity, decimal LimitPrice);

public sealed record OrderResponse(
    int Id,
    int? ParticipantId,
    string? ParticipantName,
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
    int? RelatedLoanId,
    int CreatedInCycleId,
    DateTime CreatedAt);

public sealed record MoneyTransactionDetailResponse(
    int Id,
    string Type,
    decimal Amount,
    int CreatedInCycleId,
    int? CycleNumber,
    DateTime CreatedAt,
    MoneyTransactionOrderInfo? Order,
    MoneyTransactionTradeInfo? Trade,
    MoneyTransactionLoanInfo? Loan,
    IReadOnlyList<DividendPayoutLineResponse>? DividendBreakdown);

public sealed record MoneyTransactionOrderInfo(
    int OrderId,
    int CompanyId,
    string? CompanyName,
    string Side,
    string Status,
    int Quantity,
    int FilledQuantity,
    decimal LimitPrice);

public sealed record MoneyTransactionTradeInfo(
    int ShareTransactionId,
    int CompanyId,
    string? CompanyName,
    int Quantity,
    decimal Price,
    decimal TotalCost);

public sealed record MoneyTransactionLoanInfo(
    int LoanId,
    decimal Principal,
    decimal RemainingPrincipal,
    decimal InterestRatePerCycle,
    int TermCycles,
    decimal PastDueAmount,
    string Status);

public sealed record DividendPayoutLineResponse(int CompanyId, string? CompanyName, decimal Amount);

public sealed record CycleResponse(int Id, int CycleNumber, string Status, DateTime? StartedAt, DateTime? CompletedAt);

public sealed record ShareTransactionResponse(
    int Id,
    int? SellerId,
    string? SellerName,
    int BuyerId,
    string? BuyerName,
    int CompanyId,
    int Quantity,
    decimal Price,
    decimal? MarketPriceBefore,
    decimal TotalCost,
    int CreatedInCycleId,
    DateTime CreatedAt);

public sealed record PriceSnapshotResponse(int Id, int CompanyId, decimal Price, decimal? Capitalization, int CreatedInCycleId, DateTime CreatedAt);

public sealed record ParticipantWorthPointResponse(
    int CreatedInCycleId,
    int CycleNumber,
    decimal Balance,
    decimal HoldingsValue,
    decimal LoanLiability,
    decimal TotalWorth,
    DateTime CreatedAt);

public sealed record NewsPostResponse(
    int Id,
    string Title,
    string Content,
    int PublishedInCycleId,
    int PublishedInCycleNumber,
    DateTime PublishedAt,
    string Scope,
    string Category,
    string? Direction,
    decimal? ImpactPercent,
    int? TargetCompanyId,
    string? TargetCompanyName,
    string[] IndustryNames);

public sealed record IndustryResponse(
    int Id,
    string Name,
    int SentimentValue,
    decimal SentimentVolatility,
    decimal SectorBeta,
    int LastCycleSentimentChange);

public sealed record IndustryDetailResponse(
    int Id,
    string Name,
    int SentimentValue,
    decimal SentimentVolatility,
    decimal SectorBeta,
    decimal TotalNetWorth,
    int LastCycleSentimentChange,
    int CompanyCount);

public sealed record IndustrySentimentPointResponse(
    int CreatedInCycleId,
    int CycleNumber,
    int SentimentValue,
    DateTime CreatedAt);

public sealed record IndustrySentimentHistoryResponse(
    int IndustryId,
    string IndustryName,
    IndustrySentimentPointResponse[] Points);

public sealed record NewsThemeResponse(string Key, string Label);

public sealed record CrisisResponse(
    int Id,
    string Title,
    string Content,
    string Scope,
    int TriggeredInCycleId,
    int TriggeredInCycleNumber,
    int DurationCycles,
    DateTime TriggeredAt,
    int EventCount,
    CrisisIndustryResponse[] Industries);

public sealed record CrisisIndustryResponse(int IndustryId, string IndustryName, decimal ImpactPercent);

public sealed record CrisisDetailResponse(
    int Id,
    string Title,
    string Content,
    string Scope,
    int TriggeredInCycleId,
    int TriggeredInCycleNumber,
    int DurationCycles,
    DateTime TriggeredAt,
    CrisisIndustryResponse[] Industries,
    CrisisEventResponse[] Events);

public sealed record CrisisEventResponse(
    int Id,
    string Type,
    string Description,
    int? CompanyId,
    string? CompanyName,
    int? IndustryId,
    string? IndustryName,
    decimal? ImpactPercent,
    int CreatedInCycleNumber,
    DateTime CreatedAt);

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

public sealed record MarketExitResponse(
    int Id,
    int ParticipantId,
    string ParticipantName,
    MarketExitReason Reason,
    int JoinedInCycleNumber,
    int LeftInCycleNumber,
    int OrdersPlaced,
    decimal InitialBalance,
    decimal MaxTotalWorth,
    decimal QuitBalance,
    DateTime LeftAt);
