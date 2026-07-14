using System.Collections.Concurrent;

namespace TraderAi.Services;

public enum AiTraderRuntimeStatus
{
    Waiting,
    Thinking,
    Applying,
    Error,
}

public sealed record AiTraderRuntimeSnapshot(
    AiTraderRuntimeStatus Status,
    string? Message,
    long? CurrentCallId,
    int? SnapshotCycleNumber,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? NextRetryAt)
{
    public static readonly AiTraderRuntimeSnapshot Idle =
        new(AiTraderRuntimeStatus.Waiting, null, null, null, null, null, null);
}

// Process-wide, in-memory runtime view of AI traders. It owns one cancellation scope per participant so a
// configuration edit or convert-back can cancel an in-flight call, and holds the last safe status shown in the
// API. Decrypted keys are never stored here. Status resets to Waiting when the application restarts.
public sealed class AiTraderRuntimeState
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> tokens = new();
    private readonly ConcurrentDictionary<int, AiTraderRuntimeSnapshot> snapshots = new();

    // Opens a fresh cancellation scope for a participant's next provider call, cancelling any prior one.
    public CancellationToken BeginCall(int participantId)
    {
        var cts = new CancellationTokenSource();
        if (tokens.TryRemove(participantId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        tokens[participantId] = cts;
        return cts.Token;
    }

    // Cancels a participant's in-flight call, if any, so a response that returns afterward cannot apply.
    public void Cancel(int participantId)
    {
        if (tokens.TryRemove(participantId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void Set(int participantId, AiTraderRuntimeSnapshot snapshot) => snapshots[participantId] = snapshot;

    public AiTraderRuntimeSnapshot Get(int participantId)
        => snapshots.TryGetValue(participantId, out var snapshot) ? snapshot : AiTraderRuntimeSnapshot.Idle;

    public void Clear(int participantId) => snapshots.TryRemove(participantId, out _);
}
