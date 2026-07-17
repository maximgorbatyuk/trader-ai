using TraderAi.Models;

namespace TraderAi.Api;

public sealed record ParticipantResponse(
    int Id,
    string Name,
    string Type,
    string Temperament,
    string RiskProfile,
    decimal CurrentBalance,
    decimal SettledCashBalance,
    decimal UnsettledCashBalance,
    decimal ReservedBalance,
    decimal AvailableBalance,
    int SharesOwned,
    int CompaniesOwned,
    decimal HoldingsValue,
    decimal LoanLiability,
    decimal MarginLiability,
    decimal TotalWorth,
    int PendingSettlementCount,
    int? NextSettlementDueDayNumber,
    bool IsActive,
    bool IsBankrupt,
    bool IsFavorite,
    string? AiProviderId,
    string? AiProviderLabel,
    string? AiModel,
    string? AiStatus,
    string? AiStatusMessage,
    long? AiCurrentCallId,
    int? AiMaxDecisionsPerDay,
    int? MemberOfCollectiveFundId,
    string? MemberOfCollectiveFundName);

public sealed record PagedParticipantsResponse(ParticipantResponse[] Items, int Total, int Page, int PageSize);

public sealed record ParticipantDetailResponse(
    int Id,
    string Name,
    string Type,
    string Temperament,
    string RiskProfile,
    decimal InitialBalance,
    decimal CurrentBalance,
    decimal SettledCashBalance,
    decimal UnsettledCashBalance,
    decimal ReservedBalance,
    decimal AvailableBalance,
    int SharesOwned,
    decimal HoldingsValue,
    decimal HoldingsCostBasis,
    decimal LoanLiability,
    MarginAccountResponse Margin,
    decimal TotalWorth,
    int PendingSettlementCount,
    int? NextSettlementDueDayNumber,
    bool IsActive,
    bool IsFavorite,
    string? CollectiveFundStatus,
    CollectiveFundMemberResponse[] CollectiveFundMembers,
    int? MemberOfCollectiveFundId,
    string? MemberOfCollectiveFundName,
    string? AiProviderId,
    string? AiProviderLabel,
    string? AiModel,
    string? AiStatus,
    string? AiStatusMessage,
    long? AiCurrentCallId,
    int? AiMaxDecisionsPerDay);

public sealed record CollectiveFundMemberResponse(
    int ParticipantId,
    string Name,
    string Type,
    int JoinedInCycleNumber,
    DateTime JoinedAt,
    decimal Deposit,
    decimal Payouts,
    bool IsLeaving,
    // Trading days relative to leave eligibility. Negative means the membership is still locked; zero or
    // positive means the member may take its ordinary leave rolls. Founders never switch away.
    int LeaveCountdownTradingDays,
    bool IsFounder);

public sealed record PagedFundMembersResponse(CollectiveFundMemberResponse[] Items, int Total, int Page, int PageSize);

public sealed record UpdateParticipantProfileRequest(Temperament Temperament, RiskProfile RiskProfile);

public sealed record RenameParticipantRequest(string? Name);

public sealed record AdjustParticipantCashRequest(decimal Amount);

public sealed record HoldingResponse(
    int CompanyId,
    string CompanyName,
    int Shares,
    int SettledShares,
    int PendingShares,
    decimal CurrentPrice,
    decimal MarketValue,
    decimal CostBasis);

public sealed record PagedHoldingsResponse(HoldingResponse[] Items, int Total, int Page, int PageSize);

public sealed record IndustryHoldingResponse(
    int IndustryId,
    string IndustryName,
    int CompanyCount,
    int Shares,
    decimal Value,
    decimal CostBasis,
    decimal Pnl,
    double Pct);

public sealed record PagedIndustryHoldingsResponse(IndustryHoldingResponse[] Items, int Total, int Page, int PageSize);

public sealed record MoneyTransactionResponse(
    int Id,
    string Type,
    decimal Amount,
    int? RelatedOrderId,
    int? RelatedShareTransactionId,
    int? RelatedLoanId,
    int CreatedInCycleId,
    DateTime CreatedAt);

public sealed record PagedMoneyTransactionsResponse(
    MoneyTransactionResponse[] Items,
    int Total,
    int Page,
    int PageSize);

public sealed record MoneyTransactionDetailResponse(
    int Id,
    string Type,
    decimal Amount,
    int CreatedInCycleId,
    int? CycleNumber,
    DateTime CreatedAt,
    int? FromWhomId,
    string? FromWhomName,
    string? Description,
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
    decimal PastDuePrincipal,
    decimal PastDueInterest,
    decimal AccruedFees,
    decimal TotalLiability,
    string Status);

public sealed record DividendPayoutLineResponse(int CompanyId, string? CompanyName, decimal Amount);

public sealed record ParticipantWorthPointResponse(
    int CreatedInCycleId,
    int CycleNumber,
    decimal Balance,
    decimal HoldingsValue,
    decimal LoanLiability,
    decimal MarginLiability,
    decimal TotalWorth,
    DateTime CreatedAt);

public sealed record SettlementInstructionResponse(
    int Id,
    int ShareTransactionId,
    string Side,
    int CompanyId,
    string CompanyName,
    int Quantity,
    decimal CashAmount,
    int TradeDayNumber,
    int DueDayNumber,
    string Status,
    DateTime CreatedAt,
    DateTime? SettledAt);

public sealed record PagedSettlementInstructionsResponse(
    SettlementInstructionResponse[] Items,
    int Total,
    int Page,
    int PageSize);

public sealed record MarginAccountResponse(
    decimal DebitBalance,
    decimal AccruedInterest,
    decimal TotalLiability,
    decimal AccountEquity,
    decimal BuyingPower,
    decimal InitialMarginRate,
    decimal MaintenanceMarginRate,
    decimal InitialRequirement,
    decimal MaintenanceRequirement,
    decimal MaintenanceExcess,
    decimal Deficiency,
    string? CallStatus);
