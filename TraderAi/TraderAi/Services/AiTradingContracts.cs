using System.Text.Json.Serialization;
using TraderAi.Models;

namespace TraderAi.Services;

// The exact assistant-content contract. Property names are explicit so the wire format never depends on a
// global naming policy, and deserialization is strict (see AiDecisionJson).
public sealed record AiTradeDecision(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("orders")] AiTradeOrderDecision[] Orders);

public sealed record AiTradeOrderDecision(
    [property: JsonPropertyName("side")] OrderType Side,
    [property: JsonPropertyName("companyId")] int CompanyId,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("limitPrice")] decimal LimitPrice,
    [property: JsonPropertyName("reason")] string Reason);

// The credential-free request body prepared before the audit row is written, so the exact bytes sent can be
// logged without ever containing the key.
public sealed record PreparedAiProviderRequest(
    string ProviderId,
    string ProviderLabel,
    string Model,
    Uri Endpoint,
    string RequestJson);

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
