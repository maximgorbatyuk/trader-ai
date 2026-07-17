using TraderAi.Models;

namespace TraderAi.Api;

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
    decimal SettledCashBalance,
    decimal UnsettledCashBalance,
    decimal ReservedBalance,
    decimal AvailableBalance,
    int SharesOwned,
    decimal HoldingsValue,
    decimal LoanLiability,
    MarginAccountResponse Margin,
    decimal TotalWorth,
    int PendingSettlementCount,
    int? NextSettlementDueDayNumber,
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
    int? FundPopularityIndex,
    MarginAccountResponse? FundMargin,
    int? FundPendingSettlementCount,
    int? FundNextSettlementDueDayNumber,
    decimal? FundLastCycleMoneyChange);

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
