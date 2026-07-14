namespace TraderAi.Models;

// One audited provider call. The row keeps only a scalar participant id and name with no foreign key, so the
// collected history survives an ordinary participant departure while a full market reset still clears it. No
// key or Authorization value is ever written to any text column here.
public sealed class AiTraderCall
{
    public long Id { get; set; }

    public int ParticipantId { get; set; }

    public required string ParticipantName { get; set; }

    public required string ProviderId { get; set; }

    public required string ProviderLabel { get; set; }

    public required string Model { get; set; }

    public int ConfigurationRevision { get; set; }

    public int SnapshotCycleId { get; set; }

    public int SnapshotCycleNumber { get; set; }

    public required string PromptHash { get; set; }

    public required string RequestJson { get; set; }

    public string? ResponseBody { get; set; }

    public string? DecisionJson { get; set; }

    public string? ApplicationResultJson { get; set; }

    // Denormalized so the newest-first history list projects without materializing the large request, response,
    // decision, or application-result columns.
    public string? Summary { get; set; }

    public int AppliedOrders { get; set; }

    public int RejectedOrders { get; set; }

    public AiTraderCallStatus Status { get; set; }

    public int? HttpStatusCode { get; set; }

    public string? Error { get; set; }

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public int? TotalTokens { get; set; }

    public DateTime RequestedAt { get; set; }

    public DateTime? RespondedAt { get; set; }

    public DateTime? AppliedAt { get; set; }

    public long? DurationMilliseconds { get; set; }
}
