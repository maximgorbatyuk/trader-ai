using System.Text.Json.Serialization;
using TraderAi.Models;

namespace TraderAi.Services;

// The exact assistant-content contract. Property names are explicit so the wire format never depends on a
// global naming policy, and deserialization is strict (see AiDecisionJson).
public sealed record AiTradeDecision
{
    [JsonConstructor]
    public AiTradeDecision(
        string summary,
        AiTradeOrderDecision[] orders,
        int[]? cancelOrderIds = null,
        AiBigInvestmentDecision? bigInvestment = null,
        AiTradePredictionDecision[]? predictions = null)
    {
        Summary = summary;
        Orders = orders;
        CancelOrderIds = cancelOrderIds ?? [];
        BigInvestment = bigInvestment;
        Predictions = predictions ?? [];
    }

    [JsonPropertyName("summary")]
    [JsonConverter(typeof(SummaryJsonConverter))]
    public string Summary { get; init; }

    [JsonPropertyName("orders")]
    public AiTradeOrderDecision[] Orders { get; init; }

    [JsonPropertyName("cancelOrderIds")]
    public int[] CancelOrderIds { get; init; }

    [JsonPropertyName("bigInvestment")]
    public AiBigInvestmentDecision? BigInvestment { get; init; }

    [JsonPropertyName("predictions")]
    public AiTradePredictionDecision[] Predictions { get; init; }
}

public sealed record AiTradePredictionDecision(
    [property: JsonPropertyName("companyId")] int CompanyId,
    [property: JsonPropertyName("direction")] AiPredictionDirection Direction,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("horizonCycles")] int HorizonCycles,
    [property: JsonPropertyName("targetPrice")] decimal? TargetPrice,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record AiBigInvestmentDecision
{
    [JsonConstructor]
    public AiBigInvestmentDecision(int companyId, int? shares, decimal? amount, string reason)
    {
        CompanyId = companyId;
        Shares = shares;
        Amount = amount;
        Reason = reason;
    }

    public AiBigInvestmentDecision(int companyId, int shares, string reason)
        : this(companyId, shares, null, reason)
    {
    }

    public AiBigInvestmentDecision(int companyId, decimal amount, string reason)
        : this(companyId, null, amount, reason)
    {
    }

    [JsonPropertyName("companyId")]
    public int CompanyId { get; init; }

    [JsonPropertyName("shares")]
    public int? Shares { get; init; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; }
}

// PriceOffsetPercent is a signed percentage the backend applies to the company's current market price when the
// decision is applied, not an absolute price. Returning an offset instead of a price keeps a slow provider call
// from committing to a limit that has since drifted stale; the resolved price is computed against the freshest
// price and clamped onto the allowed band.
public sealed record AiTradeOrderDecision(
    [property: JsonPropertyName("side")] OrderType Side,
    [property: JsonPropertyName("companyId")] int CompanyId,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("priceOffsetPercent")] decimal PriceOffsetPercent,
    [property: JsonPropertyName("reason")] string Reason);

// The credential-free request body prepared before the audit row is written, so the exact bytes sent can be
// logged without ever containing the key.
public sealed record PreparedAiProviderRequest(
    string ProviderId,
    string ProviderLabel,
    string Model,
    Uri Endpoint,
    string RequestJson,
    int RequestTimeoutSeconds = 300);

public enum AiProviderCallOutcome
{
    Success,
    HttpError,
    MalformedResponse,
    TimedOut,
    Cancelled,
}

// The outcome of one provider call. RawBody is preserved unchanged for the audit log even on failure, and the
// distinct outcomes let the coordinator choose the right status and retry window.
public sealed record AiProviderResponse(
    AiProviderCallOutcome Outcome,
    int? HttpStatusCode,
    string? RawBody,
    string? AssistantContent,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    TimeSpan? RetryAfter,
    string? Error);
