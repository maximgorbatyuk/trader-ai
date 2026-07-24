using TraderAi.Models;

namespace TraderAi.Api;

public sealed record PagedNewsResponse(NewsPostResponse[] Items, int Total, int Page, int PageSize);

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
    string[] IndustryNames,
    int? PortfolioAuditSummaryId);

public sealed record NewsThemeResponse(string Key, string Label);

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
