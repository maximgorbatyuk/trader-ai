using TraderAi.Models;

namespace TraderAi.Api;

public sealed record MarketResponse(
    int Id,
    string Name,
    string Status,
    int? CurrentCycleId,
    int? CurrentCycleNumber,
    decimal LastDividendTotal,
    int? TradingDayNumber,
    string? TradingSessionState,
    int? TradingCycleNumber,
    int? RemainingTradingCycles,
    int? RemainingPhaseSeconds,
    int? TradingCycleSeconds,
    int LuldAffectedCount);

public sealed record CycleResponse(int Id, int CycleNumber, string Status, DateTime? StartedAt, DateTime? CompletedAt);

public sealed record ActivityPointResponse(
    int CycleNumber,
    int TradingDayNumber,
    int TradingCycleNumber,
    int OrdersPlaced,
    bool PaidDividend);

public sealed record PriceSnapshotResponse(int Id, int CompanyId, decimal Price, decimal? Capitalization, int CreatedInCycleId, int CreatedInCycleNumber, DateTime CreatedAt);
