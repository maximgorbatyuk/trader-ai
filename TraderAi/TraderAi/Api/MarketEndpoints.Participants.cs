using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Api;

public static partial class MarketEndpoints
{
    public static void MapParticipantEndpoints(this WebApplication app)
    {
        app.MapGet("/participants", async (
            AppDbContext dbContext, MarginService marginService, AiProviderCatalog catalog, AiTraderRuntimeState aiRuntime) =>
            Results.Ok(await BuildParticipantResponsesAsync(dbContext, marginService, catalog, aiRuntime)));

        // Server-paged traders for the roster page: name search, type filter, and sortable numeric columns.
        // The array endpoint above still feeds the dashboard, which sums trader cash across the whole set.
        app.MapGet("/participants/paged", async (
            int? page, int? pageSize, string? search, string? sort, string? sortDir, string? type, string? status,
            AppDbContext dbContext,
            MarginService marginService,
            AiProviderCatalog catalog,
            AiTraderRuntimeState aiRuntime) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 20);
            var descending = SortDescending(sortDir);

            // The roster shows fund members alongside active traders, each labeled with its status.
            IEnumerable<ParticipantResponse> participants =
                await BuildParticipantResponsesAsync(dbContext, marginService, catalog, aiRuntime, includeFundMembers: true);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLowerInvariant();
                participants = participants.Where(participant => participant.Name.ToLowerInvariant().Contains(term));
            }
            if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "all", StringComparison.OrdinalIgnoreCase))
            {
                participants = participants.Where(participant => participant.Type == type);
            }
            if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            {
                participants = participants.Where(participant => participant.MemberOfCollectiveFundId is null);
            }
            else if (string.Equals(status, "in-fund", StringComparison.OrdinalIgnoreCase))
            {
                participants = participants.Where(participant => participant.MemberOfCollectiveFundId is not null);
            }

            var filtered = participants.ToList();
            IEnumerable<ParticipantResponse> ordered = sort switch
            {
                "name" => descending
                    ? filtered.OrderByDescending(participant => participant.Name, StringComparer.OrdinalIgnoreCase)
                    : filtered.OrderBy(participant => participant.Name, StringComparer.OrdinalIgnoreCase),
                "shares" => OrderRows(filtered, participant => participant.SharesOwned, descending),
                "balance" => OrderRows(filtered, participant => participant.CurrentBalance, descending),
                "holdings" => OrderRows(filtered, participant => participant.HoldingsValue, descending),
                _ => OrderRows(filtered, participant => participant.TotalWorth, descending),
            };

            var items = ordered.Skip((pageIndex - 1) * size).Take(size).ToArray();
            return Results.Ok(new PagedParticipantsResponse(items, filtered.Count, pageIndex, size));
        });

        app.MapGet("/participants/{participantId:int}", async (
            int participantId,
            AppDbContext dbContext,
            MarginService marginService,
            IOptions<CollectiveFundOptions> fundOptions,
            AiProviderCatalog catalog,
            AiTraderRuntimeState aiRuntime) =>
        {
            var detail = await BuildParticipantDetailAsync(dbContext, marginService, fundOptions.Value, catalog, aiRuntime, participantId);
            return detail is null
                ? Results.NotFound(new { error = "Participant not found." })
                : Results.Ok(detail);
        });

        app.MapPut("/participants/{participantId:int}/profile", async (
            int participantId,
            UpdateParticipantProfileRequest request,
            MarketService marketService,
            AppDbContext dbContext,
            MarginService marginService,
            IOptions<CollectiveFundOptions> fundOptions,
            AiProviderCatalog catalog,
            AiTraderRuntimeState aiRuntime) =>
        {
            var updated = await marketService.UpdateParticipantProfileAsync(
                participantId,
                request.Temperament,
                request.RiskProfile);

            return updated is null
                ? Results.NotFound(new { error = "Participant not found." })
                : Results.Ok(await BuildParticipantDetailAsync(dbContext, marginService, fundOptions.Value, catalog, aiRuntime, participantId));
        });

        app.MapPut("/participants/{participantId:int}/name", async (
            int participantId,
            RenameParticipantRequest request,
            MarketService marketService,
            AppDbContext dbContext,
            MarginService marginService,
            IOptions<CollectiveFundOptions> fundOptions,
            AiProviderCatalog catalog,
            AiTraderRuntimeState aiRuntime) =>
        {
            var name = request.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(new { error = "A participant name is required." });
            }

            var updated = await marketService.RenameParticipantAsync(participantId, name);
            return updated is null
                ? Results.NotFound(new { error = "Participant not found." })
                : Results.Ok(await BuildParticipantDetailAsync(
                    dbContext, marginService, fundOptions.Value, catalog, aiRuntime, participantId));
        });

        app.MapPost("/participants/{participantId:int}/cash-adjustments", async (
            int participantId,
            AdjustParticipantCashRequest request,
            MarketService marketService,
            AppDbContext dbContext,
            MarginService marginService,
            IOptions<CollectiveFundOptions> fundOptions,
            AiProviderCatalog catalog,
            AiTraderRuntimeState aiRuntime) =>
        {
            var result = await marketService.AdjustParticipantCashAsync(participantId, request.Amount);
            if (result.ParticipantNotFound)
            {
                return Results.NotFound(new { error = result.Error });
            }

            return result.Success
                ? Results.Ok(await BuildParticipantDetailAsync(
                    dbContext, marginService, fundOptions.Value, catalog, aiRuntime, participantId))
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapPost("/participants/{participantId:int}/fund-membership/force-leave", async (
            int participantId,
            MarketService marketService) =>
        {
            var result = await marketService.ForceParticipantToLeaveFundAsync(participantId);
            if (result.ParticipantNotFound)
            {
                return Results.NotFound(new { error = result.Error });
            }

            return result.Success
                ? Results.Ok(new { status = result.Status!.Value.ToString() })
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapGet("/ai/providers", (AiProviderCatalog catalog) =>
            Results.Ok(catalog.All
                .Select(descriptor => new AiProviderInfo(descriptor.Id, descriptor.Label, descriptor.Models.ToArray()))
                .ToArray()));

        app.MapPut("/participants/{participantId:int}/automation", async (
            int participantId,
            UpdateParticipantAutomationRequest request,
            AiTraderConfigurationService configurationService,
            AppDbContext dbContext,
            MarginService marginService,
            IOptions<CollectiveFundOptions> fundOptions,
            AiProviderCatalog catalog,
            AiTraderRuntimeState aiRuntime) =>
        {
            var result = await configurationService.UpdateAutomationAsync(participantId, request);
            if (result.ParticipantNotFound)
            {
                return Results.NotFound(new { error = result.Error });
            }

            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error });
            }

            return Results.Ok(await BuildParticipantDetailAsync(
                dbContext, marginService, fundOptions.Value, catalog, aiRuntime, participantId));
        });

        app.MapPost("/participants/{participantId:int}/automation/test", async (
            int participantId,
            TestParticipantAutomationRequest request,
            AppDbContext dbContext,
            AiProviderCatalog catalog,
            IAiProviderClient client,
            CancellationToken cancellationToken) =>
        {
            if (!await dbContext.Participants.AnyAsync(candidate => candidate.Id == participantId, cancellationToken))
            {
                return Results.NotFound(new { error = "Participant not found." });
            }

            if (!catalog.TryNormalizeProvider(request.ProviderId, out var providerId))
            {
                return Results.BadRequest(new { error = "Unknown AI provider." });
            }

            var model = request.Model?.Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                return Results.BadRequest(new { error = "A model name is required." });
            }

            var apiKey = catalog.FindApiKey(providerId);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Results.BadRequest(new { error = "No API key is configured for this provider. Add it in Settings." });
            }

            var provider = catalog.Find(providerId)!;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var response = await client.SendTestAsync(provider, model, apiKey, cancellationToken);
            stopwatch.Stop();
            var success = response.Outcome == AiProviderCallOutcome.Success;
            return Results.Ok(new AiProviderTestResponse(
                success,
                response.HttpStatusCode,
                response.AssistantContent,
                response.RawBody,
                stopwatch.ElapsedMilliseconds,
                success ? null : response.Error ?? "The provider call failed."));
        });

        app.MapGet("/participants/{participantId:int}/ai-calls", async (
            int participantId, int? page, int? pageSize, AiTraderCallService callService) =>
            Results.Ok(await callService.GetPageAsync(participantId, page ?? 1, pageSize ?? 20)));

        app.MapGet("/participants/{participantId:int}/ai-decision-quality", async (
            int participantId, AiTraderCallService callService) =>
            Results.Ok(await callService.GetDecisionQualityAsync(participantId)));

        app.MapGet("/participants/{participantId:int}/ai-calls/{callId:long}", async (
            int participantId, long callId, AiTraderCallService callService) =>
        {
            var call = await callService.GetCallAsync(participantId, callId);
            return call is null
                ? Results.NotFound(new { error = "Call not found." })
                : Results.Ok(new AiTraderCallDetailResponse(
                    call.Id,
                    call.ProviderId,
                    call.ProviderLabel,
                    call.Model,
                    call.Status.ToString(),
                    call.SnapshotCycleNumber,
                    call.PromptHash,
                    call.RequestJson,
                    call.ResponseBody,
                    call.DecisionJson,
                    call.ApplicationResultJson,
                    call.Summary,
                    call.AppliedOrders,
                    call.RejectedOrders,
                    call.Error,
                    call.PromptTokens,
                    call.CompletionTokens,
                    call.TotalTokens,
                    call.DurationMilliseconds,
                    call.RequestedAt,
                    call.RespondedAt,
                    call.AppliedAt));
        });

        app.MapGet("/participants/{participantId:int}/holdings", async (int participantId, AppDbContext dbContext) =>
        {
            // AverageCost is the weighted-average price this owner paid for the position, so cost basis is
            // quantity times it.
            var sharesByCompany = await dbContext.Holdings
                .Where(holding => holding.ParticipantId == participantId && holding.Quantity > 0)
                .Select(holding => new
                {
                    holding.CompanyId,
                    Shares = holding.Quantity,
                    SettledShares = holding.SettledQuantity,
                    CostBasis = holding.Quantity * holding.AverageCost,
                })
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
                        holding.SettledShares,
                        holding.Shares - holding.SettledShares,
                        currentPrice,
                        currentPrice * holding.Shares,
                        holding.CostBasis);
                })
                .ToArray();

            return Results.Ok(response);
        });

        // Server-paged, sortable variant of the holdings list. Rows are computed then ordered/paged in memory
        // because value and P/L depend on the live latest price rather than a stored column.
        app.MapGet("/participants/{participantId:int}/holdings/paged", async (
            int participantId, int? page, int? pageSize, string? sort, string? sortDir, AppDbContext dbContext) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 10);
            var descending = SortDescending(sortDir);

            var sharesByCompany = await dbContext.Holdings
                .Where(holding => holding.ParticipantId == participantId && holding.Quantity > 0)
                .Select(holding => new
                {
                    holding.CompanyId,
                    Shares = holding.Quantity,
                    SettledShares = holding.SettledQuantity,
                    CostBasis = holding.Quantity * holding.AverageCost,
                })
                .ToListAsync();

            var companyNameById = await dbContext.Companies
                .ToDictionaryAsync(company => company.Id, company => company.Name);
            var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);

            var rows = sharesByCompany
                .Select(holding =>
                {
                    var currentPrice = latestPriceByCompany.GetValueOrDefault(holding.CompanyId);
                    return new HoldingResponse(
                        holding.CompanyId,
                        companyNameById.GetValueOrDefault(holding.CompanyId, $"#{holding.CompanyId}"),
                        holding.Shares,
                        holding.SettledShares,
                        holding.Shares - holding.SettledShares,
                        currentPrice,
                        currentPrice * holding.Shares,
                        holding.CostBasis);
                })
                .ToList();

            IEnumerable<HoldingResponse> ordered = sort switch
            {
                "company" => descending
                    ? rows.OrderByDescending(row => row.CompanyName, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderBy(row => row.CompanyName, StringComparer.OrdinalIgnoreCase),
                "settled" => OrderRows(rows, row => row.SettledShares, descending),
                "pending" => OrderRows(rows, row => row.PendingShares, descending),
                "cost" => OrderRows(rows, row => row.CostBasis, descending),
                "value" => OrderRows(rows, row => row.MarketValue, descending),
                "pnl" => OrderRows(rows, row => row.MarketValue - row.CostBasis, descending),
                _ => OrderRows(rows, row => row.Shares, descending),
            };

            var items = ordered.Skip((pageIndex - 1) * size).Take(size).ToArray();
            return Results.Ok(new PagedHoldingsResponse(items, rows.Count, pageIndex, size));
        });

        // Server-paged, sortable portfolio-by-industry aggregate. The share of portfolio is always relative to the
        // whole portfolio's value, so the total is computed across every industry before the page is taken.
        app.MapGet("/participants/{participantId:int}/portfolio-by-industry/paged", async (
            int participantId, int? page, int? pageSize, string? sort, string? sortDir, AppDbContext dbContext) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 10);
            var descending = SortDescending(sortDir);

            var holdings = await dbContext.Holdings
                .Where(holding => holding.ParticipantId == participantId && holding.Quantity > 0)
                .Select(holding => new
                {
                    holding.CompanyId,
                    Shares = holding.Quantity,
                    CostBasis = holding.Quantity * holding.AverageCost,
                })
                .ToListAsync();

            var industryIdByCompany = await dbContext.Companies
                .ToDictionaryAsync(company => company.Id, company => company.IndustryId);
            var industryNameById = await IndustryNameByIdAsync(dbContext);
            var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);

            const string unknownIndustry = "Unknown industry";
            var buckets = new Dictionary<int, IndustryBucket>();
            foreach (var holding in holdings)
            {
                var industryId = industryIdByCompany.GetValueOrDefault(holding.CompanyId);
                if (!buckets.TryGetValue(industryId, out var bucket))
                {
                    bucket = new IndustryBucket(industryId, industryNameById.GetValueOrDefault(industryId, unknownIndustry));
                    buckets[industryId] = bucket;
                }

                bucket.CompanyCount += 1;
                bucket.Shares += holding.Shares;
                bucket.Value += latestPriceByCompany.GetValueOrDefault(holding.CompanyId) * holding.Shares;
                bucket.CostBasis += holding.CostBasis;
            }

            var totalValue = buckets.Values.Sum(bucket => bucket.Value);
            var rows = buckets.Values
                .Select(bucket => new IndustryHoldingResponse(
                    bucket.IndustryId,
                    bucket.IndustryName,
                    bucket.CompanyCount,
                    bucket.Shares,
                    bucket.Value,
                    bucket.CostBasis,
                    bucket.Value - bucket.CostBasis,
                    totalValue > 0 ? (double)(bucket.Value / totalValue) : 0d))
                .ToList();

            IEnumerable<IndustryHoldingResponse> ordered = sort switch
            {
                "industry" => descending
                    ? rows.OrderByDescending(row => row.IndustryName, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderBy(row => row.IndustryName, StringComparer.OrdinalIgnoreCase),
                "companies" => OrderRows(rows, row => row.CompanyCount, descending),
                "shares" => OrderRows(rows, row => row.Shares, descending),
                "pct" => OrderRows(rows, row => row.Pct, descending),
                "pnl" => OrderRows(rows, row => row.Pnl, descending),
                _ => OrderRows(rows, row => row.Value, descending),
            };

            var items = ordered.Skip((pageIndex - 1) * size).Take(size).ToArray();
            return Results.Ok(new PagedIndustryHoldingsResponse(items, rows.Count, pageIndex, size));
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
                .Where(group => group.OrderByDescending(rating => rating.Id).First().Rating
                    is CompanyRiskRating.High or CompanyRiskRating.Extra)
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

        app.MapGet("/participants/{participantId:int}/investments", async (int participantId, int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 20, 1, 100);
            var investments = await dbContext.CompanyInvestments
                .Where(investment => investment.InvestorParticipantId == participantId)
                .OrderByDescending(investment => investment.Id)
                .Take(limit)
                .ToListAsync();

            return Results.Ok(await ToInvestmentResponsesAsync(dbContext, investments));
        });

        // Server-paged, sortable variant of the investments list. Enrichment (names, cycle numbers) happens for the
        // full set before ordering so the Company column and cycle-derived columns sort correctly across pages.
        app.MapGet("/participants/{participantId:int}/investments/paged", async (
            int participantId, int? page, int? pageSize, string? sort, string? sortDir, AppDbContext dbContext) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 10);
            var descending = SortDescending(sortDir);

            var investments = await dbContext.CompanyInvestments
                .Where(investment => investment.InvestorParticipantId == participantId)
                .ToListAsync();
            var rows = await ToInvestmentResponsesAsync(dbContext, investments);

            IEnumerable<InvestmentResponse> ordered = sort switch
            {
                "company" => descending
                    ? rows.OrderByDescending(row => row.CompanyName, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderBy(row => row.CompanyName, StringComparer.OrdinalIgnoreCase),
                "dealValue" => OrderRows(rows, row => row.DealValue, descending),
                "shares" => OrderRows(rows, row => row.SharesIssued, descending),
                "stake" => OrderRows(rows, row => row.InvestorSharePercent, descending),
                "capBefore" => OrderRows(rows, row => row.CapitalizationBeforeDeal, descending),
                "capAfter" => OrderRows(rows, row => row.FinalCapitalization, descending),
                _ => OrderRows(rows, row => row.Id, descending),
            };

            var items = ordered.Skip((pageIndex - 1) * size).Take(size).ToArray();
            return Results.Ok(new PagedInvestmentsResponse(items, rows.Length, pageIndex, size));
        });

        // Server-paged, sortable fund-members list. Defaults to largest depositor first. Reuses the same member
        // builder as the participant-detail response, then orders and pages in memory.
        app.MapGet("/participants/{participantId:int}/fund-members/paged", async (
            int participantId, int? page, int? pageSize, string? sort, string? sortDir,
            AppDbContext dbContext, IOptions<CollectiveFundOptions> fundOptions) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 10);
            var descending = SortDescending(sortDir);

            var (_, members) = await BuildCollectiveFundMembersAsync(dbContext, fundOptions.Value, participantId);

            IEnumerable<CollectiveFundMemberResponse> ordered = sort switch
            {
                "member" => descending
                    ? members.OrderByDescending(row => row.Name, StringComparer.OrdinalIgnoreCase)
                    : members.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
                "type" => descending
                    ? members.OrderByDescending(row => row.Type, StringComparer.OrdinalIgnoreCase)
                    : members.OrderBy(row => row.Type, StringComparer.OrdinalIgnoreCase),
                "joined" => OrderRows(members, row => row.JoinedInCycleNumber, descending),
                "payouts" => OrderRows(members, row => row.Payouts, descending),
                "leave" => OrderRows(members, row => row.LeaveCountdownTradingDays, descending),
                _ => OrderRows(members, row => row.Deposit, descending),
            };

            var items = ordered.Skip((pageIndex - 1) * size).Take(size).ToArray();
            return Results.Ok(new PagedFundMembersResponse(items, members.Length, pageIndex, size));
        });

        app.MapGet("/participants/{participantId:int}/share-transactions", async (int participantId, int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 10, 1, 100);
            var transactions = await dbContext.ShareTransactions
                .Where(transaction => transaction.SellerId == participantId || transaction.BuyerId == participantId)
                .Include(transaction => transaction.SettlementInstruction)
                .OrderByDescending(transaction => transaction.Id)
                .Take(limit)
                .ToListAsync();

            return Results.Ok(transactions.Select(ToShareTransactionResponse).ToArray());
        });

        app.MapGet("/participants/{participantId:int}/settlements", async (
            int participantId,
            string? status,
            int? page,
            int? pageSize,
            AppDbContext dbContext) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 20);
            var query = dbContext.SettlementInstructions
                .Where(instruction => instruction.BuyerId == participantId || instruction.SellerId == participantId);
            if (!string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            {
                var requested = string.Equals(status, "settled", StringComparison.OrdinalIgnoreCase)
                    ? SettlementStatus.Settled
                    : SettlementStatus.Pending;
                query = query.Where(instruction => instruction.Status == requested);
            }

            var total = await query.CountAsync();
            var instructions = await query
                .OrderBy(instruction => instruction.DueDayNumber)
                .ThenBy(instruction => instruction.Id)
                .Skip((pageIndex - 1) * size)
                .Take(size)
                .ToListAsync();
            var companyIds = instructions.Select(instruction => instruction.CompanyId).Distinct().ToList();
            var companyNames = await dbContext.Companies
                .Where(company => companyIds.Contains(company.Id))
                .ToDictionaryAsync(company => company.Id, company => company.Name);

            var items = instructions.Select(instruction => new SettlementInstructionResponse(
                instruction.Id,
                instruction.ShareTransactionId,
                instruction.BuyerId == participantId ? "Buy" : "Sell",
                instruction.CompanyId,
                companyNames.GetValueOrDefault(instruction.CompanyId, $"#{instruction.CompanyId}"),
                instruction.Quantity,
                instruction.CashAmount,
                instruction.TradeDayNumber,
                instruction.DueDayNumber,
                instruction.Status.ToString(),
                instruction.CreatedAt,
                instruction.SettledAt)).ToArray();
            return Results.Ok(new PagedSettlementInstructionsResponse(items, total, pageIndex, size));
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

        app.MapGet("/participants/{participantId:int}/money-transactions/paged", async (
            int participantId,
            int? page,
            int? pageSize,
            AppDbContext dbContext) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 10);
            var query = dbContext.MoneyTransactions
                .Where(transaction => transaction.ParticipantId == participantId);
            var total = await query.CountAsync();
            var transactions = await query
                .OrderByDescending(transaction => transaction.Id)
                .Skip((pageIndex - 1) * size)
                .Take(size)
                .ToListAsync();

            var items = transactions
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

            return Results.Ok(new PagedMoneyTransactionsResponse(items, total, pageIndex, size));
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

            // The source is a plain id, so the participant may already have left the market; a null name just
            // means the row survived its sender.
            var fromWhomName = transaction.FromWhomId is int fromWhomId
                ? await dbContext.Participants
                    .Where(participant => participant.Id == fromWhomId)
                    .Select(participant => participant.Name)
                    .FirstOrDefaultAsync()
                : null;

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
                transaction.FromWhomId,
                fromWhomName,
                transaction.Description,
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
                        loan.InterestRate,
                        loan.TermTradingDays,
                        loan.PastDuePrincipal,
                        loan.PastDueInterest,
                        loan.AccruedFees,
                        loan.TotalLiability,
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
                    snapshot.MarginLiability,
                    snapshot.Balance + snapshot.HoldingsValue - snapshot.LoanLiability - snapshot.MarginLiability,
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
            var (pageIndex, size) = ResolvePaging(page, pageSize, 20);

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

        app.MapGet("/participants/{participantId:int}/loans", async (int participantId, string? status, AppDbContext dbContext) =>
        {
            var query = dbContext.Loans.Where(loan => loan.ParticipantId == participantId);
            query = string.Equals(status, "all", StringComparison.OrdinalIgnoreCase)
                ? query
                : query.Where(loan => loan.Status == LoanStatus.Open);

            var loans = await query.OrderByDescending(loan => loan.Id).ToListAsync();
            return Results.Ok((await BuildLoanResponsesAsync(dbContext, loans)).ToArray());
        });
    }
}
