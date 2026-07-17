using TraderAi.Models;

namespace TraderAi.Api;

public sealed record PlaceOrderRequest(int ParticipantId, int CompanyId, OrderType Type, int Quantity, decimal LimitPrice);

public sealed record InvestInCompanyRequest(int ParticipantId, decimal Amount);

public sealed record InvestInCompanyResponse(int SharesMinted);

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
    DateTime CreatedAt,
    int? TradeDayNumber,
    int? DueDayNumber,
    string? SettlementStatus);

public sealed record PagedShareTransactionsResponse(
    ShareTransactionResponse[] Items,
    int Total,
    int Page,
    int PageSize);

public sealed record InvestmentResponse(
    int Id,
    int CompanyId,
    string CompanyName,
    int InvestorParticipantId,
    string? InvestorName,
    decimal DealValue,
    int SharesIssued,
    int SharesBeforeDeal,
    int? TradingDayNumber,
    int CreatedInCycleId,
    int CreatedInCycleNumber,
    int CyclesAgo,
    decimal CapitalizationBeforeDeal,
    decimal FinalCapitalization,
    decimal InvestorSharePercent,
    DateTime CreatedAt);

public sealed record PagedInvestmentsResponse(InvestmentResponse[] Items, int Total, int Page, int PageSize);
