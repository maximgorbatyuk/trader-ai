using TraderAi.Models;

namespace TraderAi.Api;

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
    decimal PastDuePrincipal,
    decimal PastDueInterest,
    decimal AccruedFees,
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
