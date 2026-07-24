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

        app.MapGet("/companies/{companyId:int}", async (
            int companyId,
            AppDbContext dbContext,
            IOptions<VolatilityHaltOptions> haltOptions,
            IOptions<CompanyFinancialOptions> financialOptions) =>
        {
            var detail = await BuildCompanyDetailAsync(
                dbContext,
                companyId,
                haltOptions.Value,
                financialOptions.Value);
            return detail is null
                ? Results.NotFound(new { error = "Company not found." })
                : Results.Ok(detail);
        });

        app.MapGet("/companies/{companyId:int}/financials", async (
            int companyId,
            int? page,
            int? pageSize,
            AppDbContext dbContext,
            IOptions<CompanyFinancialOptions> financialOptions) =>
        {
            if (!await dbContext.Companies.AnyAsync(company => company.Id == companyId))
            {
                return Results.NotFound(new { error = "Company not found." });
            }

            var (pageIndex, size) = ResolvePaging(page, pageSize, 20);
            var query = dbContext.CompanyFinancialSnapshots
                .AsNoTracking()
                .Where(snapshot => snapshot.CompanyId == companyId);
            var total = await query.CountAsync();

            // The extra older row is the previous observation for the last visible row, including at page boundaries.
            var rows = await query
                .Include(snapshot => snapshot.LatestDividendEvent)
                .OrderByDescending(snapshot => snapshot.TradingDayNumber)
                .ThenByDescending(snapshot => snapshot.Moment)
                .ThenByDescending(snapshot => snapshot.CreatedInCycleId)
                .ThenByDescending(snapshot => snapshot.CreatedAt)
                .ThenByDescending(snapshot => snapshot.Id)
                .Skip((pageIndex - 1) * size)
                .Take(size + 1)
                .ToListAsync();

            var visibleCount = Math.Min(size, rows.Count);
            var items = new CompanyFinancialHistoryItemResponse[visibleCount];
            for (var index = 0; index < visibleCount; index++)
            {
                var current = rows[index];
                var previous = index + 1 < rows.Count ? rows[index + 1] : null;
                items[index] = new CompanyFinancialHistoryItemResponse(
                    ToCompanyFinancialSummaryResponse(current, financialOptions.Value),
                    previous is null ? null : ToCompanyFinancialValuesResponse(previous),
                    previous is null ? null : ToAbsoluteFinancialDeltaResponse(current, previous),
                    previous is null ? null : ToPercentageFinancialDeltaResponse(current, previous));
            }

            return Results.Ok(new PagedCompanyFinancialsResponse(items, total, pageIndex, size));
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

        app.MapGet("/companies/{companyId:int}/audits", async (
            int companyId,
            int? page,
            int? pageSize,
            AppDbContext dbContext) =>
        {
            var companyName = await dbContext.Companies
                .Where(company => company.Id == companyId)
                .Select(company => company.Name)
                .FirstOrDefaultAsync();
            if (companyName is null)
            {
                return Results.NotFound(new { error = "Company not found." });
            }

            var (pageIndex, size) = ResolvePaging(page, pageSize, 20);
            var query = dbContext.CompanyRatings
                .AsNoTracking()
                .Where(rating => rating.CompanyId == companyId);
            var total = await query.CountAsync();
            var rows = await query
                .Include(rating => rating.Evidence)
                    .ThenInclude(evidence => evidence!.LatestDividendEvent)
                .Include(rating => rating.Evidence)
                    .ThenInclude(evidence => evidence!.CompanyFinancialSnapshot)
                        .ThenInclude(snapshot => snapshot!.LatestDividendEvent)
                .OrderByDescending(rating => rating.Id)
                .Skip((pageIndex - 1) * size)
                .Take(size)
                .ToListAsync();

            var auditorNames = await dbContext.Auditors
                .Where(auditor => rows.Select(rating => rating.AuditorId).Contains(auditor.Id))
                .ToDictionaryAsync(auditor => auditor.Id, auditor => auditor.Name);
            var cycleNumbers = await dbContext.MarketCycles
                .Where(cycle => rows.Select(rating => rating.CreatedInCycleId).Contains(cycle.Id))
                .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);
            var items = rows
                .Select(rating => ToCompanyAuditSummaryResponse(
                    rating,
                    companyName,
                    auditorNames.GetValueOrDefault(rating.AuditorId, $"#{rating.AuditorId}"),
                    cycleNumbers.GetValueOrDefault(rating.CreatedInCycleId)))
                .ToArray();

            return Results.Ok(new PagedCompanyAuditsResponse(items, total, pageIndex, size));
        });

        app.MapGet("/companies/{companyId:int}/audits/{auditId:int}", async (
            int companyId,
            int auditId,
            AppDbContext dbContext,
            IOptions<CompanyFinancialOptions> financialOptions) =>
        {
            var companyName = await dbContext.Companies
                .Where(company => company.Id == companyId)
                .Select(company => company.Name)
                .FirstOrDefaultAsync();
            if (companyName is null)
            {
                return Results.NotFound(new { error = "Company not found." });
            }

            var rating = await dbContext.CompanyRatings
                .AsNoTracking()
                .Where(candidate => candidate.Id == auditId && candidate.CompanyId == companyId)
                .Include(candidate => candidate.Evidence)
                    .ThenInclude(evidence => evidence!.LatestDividendEvent)
                .Include(candidate => candidate.Evidence)
                    .ThenInclude(evidence => evidence!.CompanyFinancialSnapshot)
                        .ThenInclude(snapshot => snapshot!.LatestDividendEvent)
                .FirstOrDefaultAsync();
            if (rating is null)
            {
                return Results.NotFound(new { error = "Audit not found." });
            }

            var auditorName = await dbContext.Auditors
                .Where(auditor => auditor.Id == rating.AuditorId)
                .Select(auditor => auditor.Name)
                .FirstOrDefaultAsync() ?? $"#{rating.AuditorId}";
            var createdInCycleNumber = await dbContext.MarketCycles
                .Where(cycle => cycle.Id == rating.CreatedInCycleId)
                .Select(cycle => cycle.CycleNumber)
                .FirstOrDefaultAsync();

            AuditDenominationEventResponse[] denominationEvents = [];
            AuditShareEmissionEventResponse[] freeShareEmissionEvents = [];
            if (rating.Evidence is CompanyAuditEvidence evidence)
            {
                var denominationRows = await (
                        from denomination in dbContext.StockDenominationEvents.AsNoTracking()
                        join cycle in dbContext.MarketCycles.AsNoTracking()
                            on denomination.EffectiveInCycleId equals cycle.Id
                        join day in dbContext.TradingDays.AsNoTracking()
                            on cycle.TradingDayId equals day.Id
                        where denomination.CompanyId == companyId
                            && day.DayNumber >= evidence.EvaluationStartTradingDayNumber
                            && day.DayNumber <= evidence.EvaluationEndTradingDayNumber
                        orderby denomination.Id
                        select new
                        {
                            Event = denomination,
                            CycleNumber = cycle.CycleNumber,
                            TradingDayNumber = day.DayNumber,
                        })
                    .ToListAsync();
                denominationEvents = denominationRows
                    .Select(row => new AuditDenominationEventResponse(
                        row.Event.Id,
                        row.Event.ActionType.ToString(),
                        row.Event.Ratio,
                        row.Event.IssuedSharesBefore,
                        row.Event.IssuedSharesAfter,
                        row.Event.PriceBefore,
                        row.Event.PriceAfter,
                        row.Event.EffectiveInCycleId,
                        row.CycleNumber,
                        row.TradingDayNumber,
                        row.Event.CreatedAt))
                    .ToArray();

                var emissionRows = await (
                        from emission in dbContext.ShareEmissions.AsNoTracking()
                        join cycle in dbContext.MarketCycles.AsNoTracking()
                            on emission.CreatedInCycleId equals cycle.Id
                        join day in dbContext.TradingDays.AsNoTracking()
                            on cycle.TradingDayId equals day.Id
                        where emission.CompanyId == companyId
                            && day.DayNumber >= evidence.EvaluationStartTradingDayNumber
                            && day.DayNumber <= evidence.EvaluationEndTradingDayNumber
                        orderby emission.Id
                        select new
                        {
                            Event = emission,
                            CycleNumber = cycle.CycleNumber,
                            TradingDayNumber = day.DayNumber,
                        })
                    .ToListAsync();
                freeShareEmissionEvents = emissionRows
                    .Select(row => new AuditShareEmissionEventResponse(
                        row.Event.Id,
                        row.Event.SharesEmitted,
                        row.Event.RecipientCount,
                        row.Event.CreatedInCycleId,
                        row.CycleNumber,
                        row.TradingDayNumber,
                        row.Event.CreatedAt))
                    .ToArray();
            }

            var audit = rating.Evidence;
            return Results.Ok(new CompanyAuditDetailResponse(
                rating.Id,
                rating.CompanyId,
                companyName,
                rating.Rating.ToString(),
                rating.ImpactPercent,
                rating.AuditorId,
                auditorName,
                rating.CreatedInCycleId,
                createdInCycleNumber,
                rating.CreatedAt,
                audit is not null,
                audit?.EvaluationStartTradingDayNumber,
                audit?.EvaluationEndTradingDayNumber,
                audit?.EffectiveTradingDayNumber,
                audit?.TotalScore,
                audit?.AdjustedReturnScore,
                audit?.CycleJumpScore,
                audit?.FreeShareEmissionScore,
                audit?.DenominationScore,
                audit?.DividendOutcomeScore,
                audit?.DividendCoverageScore,
                audit?.IndustryScore,
                audit?.ProfitabilityFactorScore,
                audit?.StabilityFactorScore,
                audit?.ClosureRiskFactorScore,
                audit?.ManagementOutlookFactorScore,
                audit?.StartPrice,
                audit?.EndPrice,
                audit?.AdjustedReturnPercent,
                audit?.MaximumAdjustedCycleMovePercent,
                audit?.OpeningIssuedShares,
                audit?.EmittedShares,
                audit?.FreeShareDilutionPercent,
                audit?.StockSplitCount,
                audit?.ReverseSplitCount,
                audit?.LatestDividendEvent is null
                    ? null
                    : ToCompanyDividendEventResponse(audit.LatestDividendEvent),
                audit?.IssuerCash,
                audit?.ModeledMaximumDividend,
                audit?.DividendCoverageRatio,
                audit?.OpeningIndustrySentiment,
                audit?.ClosingIndustrySentiment,
                audit?.IndustryTrend.ToString(),
                audit?.CompanyFinancialSnapshot is null
                    ? null
                    : ToCompanyFinancialSummaryResponse(
                        audit.CompanyFinancialSnapshot,
                        financialOptions.Value),
                denominationEvents,
                freeShareEmissionEvents));
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
                .Include(post => post.PortfolioAuditSummary)
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
