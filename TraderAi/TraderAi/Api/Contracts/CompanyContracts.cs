using TraderAi.Models;

namespace TraderAi.Api;

public sealed record CompanyPositionResponse(
    int Shares,
    decimal OwnershipPct,
    decimal MarketValue);

public sealed record CompanyResponse(
    int Id,
    string Name,
    bool IsFavorite,
    int IndustryId,
    string? IndustryName,
    int IssuedSharesCount,
    decimal? CurrentPrice,
    decimal PriceChangePct,
    string? CurrentRating,
    bool IsHalted,
    string LuldState,
    string? LimitDirection,
    decimal? ReferencePrice,
    decimal? LowerBandPrice,
    decimal? UpperBandPrice,
    decimal? MinimumOrderPrice,
    decimal? MaximumOrderPrice,
    int? LimitStateStartedCycleNumber,
    int? PauseUntilCycleNumber,
    CompanyPositionResponse? PlayerPosition,
    CompanyPositionResponse? FundPosition);

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

public sealed record CompanyDetailResponse(
    int Id,
    string Name,
    bool IsFavorite,
    int IndustryId,
    string? IndustryName,
    int IssuedSharesCount,
    decimal? CurrentPrice,
    decimal PriceChangePct,
    decimal MarketCap,
    decimal IssuerCash,
    int SharesHeldByIssuer,
    int SharesOutstanding,
    int ShareholderCount,
    DateTime CreatedAt,
    string? CurrentRating,
    string? PreviousRating,
    bool IsClosed,
    int? ClosedInCycleNumber,
    bool IsHalted,
    int? HaltedUntilCycleNumber,
    string LuldState,
    string? LimitDirection,
    decimal? ReferencePrice,
    decimal? LowerBandPrice,
    decimal? UpperBandPrice,
    decimal? MinimumOrderPrice,
    decimal? MaximumOrderPrice,
    int? LimitStateStartedCycleNumber,
    int? PauseUntilCycleNumber,
    int RemainingPauseCycles,
    int RemainingPauseSeconds);

public sealed record CorporateCashMovementResponse(
    int Id,
    string Type,
    decimal Amount,
    int CreatedInCycleId,
    int CreatedInCycleNumber,
    DateTime CreatedAt);

public sealed record PagedCorporateCashMovementsResponse(
    CorporateCashMovementResponse[] Items,
    int Total,
    int Page,
    int PageSize);

public sealed record ShareholderResponse(
    int OwnerId,
    string OwnerName,
    int Shares,
    decimal MarketValue,
    decimal CostBasis,
    decimal PctOfIssued);

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
