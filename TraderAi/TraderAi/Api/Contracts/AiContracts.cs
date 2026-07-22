using TraderAi.Models;

namespace TraderAi.Api;

// Distinct from the provider HTTP-client result AiProviderResponse in the Services layer.
public sealed record AiProviderInfo(string Id, string Label, string[] Models);

public sealed record TestParticipantAutomationRequest(string ProviderId, string Model);

public sealed record AiProviderTestResponse(
    bool Success,
    int? HttpStatusCode,
    string? AssistantContent,
    string? ResponseBody,
    long? DurationMilliseconds,
    string? Error);

public sealed record AiPredictionResponse(
    long Id,
    int CompanyId,
    int SnapshotCycleNumber,
    int SnapshotTradingDayNumber,
    decimal BaselinePrice,
    string Direction,
    decimal Confidence,
    int HorizonCycles,
    decimal? TargetPrice,
    string Reason);

public sealed record AiTraderCallDetailResponse(
    long Id,
    string ProviderId,
    string ProviderLabel,
    string Model,
    string Status,
    int SnapshotCycleNumber,
    string PromptHash,
    string RequestJson,
    string? ResponseBody,
    string? DecisionJson,
    string? ApplicationResultJson,
    string? Summary,
    int AppliedOrders,
    int RejectedOrders,
    string? Error,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    long? DurationMilliseconds,
    DateTime RequestedAt,
    DateTime? RespondedAt,
    DateTime? AppliedAt,
    AiPredictionResponse[] Predictions);
