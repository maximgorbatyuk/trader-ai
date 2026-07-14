namespace TraderAi.Models;

// One provider configuration per AI-driven participant. ParticipantId is both the primary key and the foreign
// key, so a participant has at most one configuration and it cascades away when the participant is deleted. The
// key is stored as provided; it is never returned by the API and never written to a log.
public sealed class AiTraderConfiguration
{
    public int ParticipantId { get; set; }

    public required string ProviderId { get; set; }

    public required string Model { get; set; }

    public required string ApiKey { get; set; }

    // Bumped on every effective edit so a provider response that returns after the configuration changed is
    // recognised as stale and discarded instead of applied.
    public int Revision { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
