namespace TraderAi.Models;

// One provider configuration per AI-driven participant. ParticipantId is both the primary key and the foreign
// key, so a participant has at most one configuration and it cascades away when the participant is deleted. The
// connection key is not stored here; it is a per-provider setting resolved from the settings database at send time.
public sealed class AiTraderConfiguration
{
    public int ParticipantId { get; set; }

    public required string ProviderId { get; set; }

    public required string Model { get; set; }

    // Bumped on every effective edit so a provider response that returns after the configuration changed is
    // recognised as stale and discarded instead of applied.
    public int Revision { get; set; }

    // Maximum provider decisions this agent makes per trading day. The coordinator spreads these across the day's
    // cycles; the final one is an end-of-day planning call.
    public int MaxDecisionsPerDay { get; set; } = 3;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
