using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Api;

public static partial class MarketEndpoints
{
    public static void MapNewsEndpoints(this WebApplication app)
    {
        app.MapGet("/news", async (int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 30, 1, 200);
            var posts = await dbContext.NewsPosts
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

        // Server-paged news for the News page. News grows unbounded across cycles, so this pages in the
        // database rather than trimming a client-loaded list like the dashboard newswire does.
        app.MapGet("/news/paged", async (int? page, int? pageSize, AppDbContext dbContext) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 20);

            var total = await dbContext.NewsPosts.CountAsync();
            var posts = await dbContext.NewsPosts
                .OrderByDescending(post => post.Id)
                .Skip((pageIndex - 1) * size)
                .Take(size)
                .Include(post => post.Industries)
                .Include(post => post.PortfolioAuditSummary)
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

        app.MapGet("/portfolio-audit-summaries/{summaryId:int}", async (
            int summaryId,
            AppDbContext dbContext) =>
        {
            var summary = await dbContext.PortfolioAuditSummaries
                .AsNoTracking()
                .Include(candidate => candidate.Items)
                    .ThenInclude(item => item.CompanyRating)
                        .ThenInclude(rating => rating!.Evidence)
                .FirstOrDefaultAsync(candidate => candidate.Id == summaryId);
            if (summary is null)
            {
                return Results.NotFound(new { error = "Portfolio audit summary not found." });
            }

            var companyIds = summary.Items.Select(item => item.CompanyId).Distinct().ToArray();
            var companyNames = await dbContext.Companies
                .Where(company => companyIds.Contains(company.Id))
                .ToDictionaryAsync(company => company.Id, company => company.Name);
            var items = summary.Items
                .OrderBy(item => item.CompanyId)
                .Select(item => new PortfolioAuditSummaryItemResponse(
                    item.Id,
                    item.CompanyId,
                    companyNames.GetValueOrDefault(item.CompanyId, $"#{item.CompanyId}"),
                    item.CompanyRatingId,
                    item.PlayerQuantity,
                    item.ManagedFundQuantity,
                    item.CompanyRating?.Rating.ToString() ?? string.Empty,
                    item.CompanyRating?.Evidence?.TotalScore,
                    item.CompanyRating?.Evidence?.AdjustedReturnPercent,
                    item.CompanyRating?.Evidence?.DividendCoverageRatio,
                    item.CompanyRating?.Evidence?.IndustryTrend.ToString()))
                .ToArray();

            return Results.Ok(new PortfolioAuditSummaryResponse(
                summary.Id,
                summary.NewsPostId,
                summary.EvaluationStartTradingDayNumber,
                summary.EvaluationEndTradingDayNumber,
                summary.EffectiveTradingDayNumber,
                summary.ExtraRaisedExpectationsCount,
                summary.RaisedExpectationsCount,
                summary.StableCount,
                summary.LowRiskCount,
                summary.HighRiskCount,
                summary.AverageScore,
                summary.OverallDirection.ToString(),
                summary.CreatedAt,
                items));
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
                .Include(post => post.PortfolioAuditSummary)
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
    }
}
