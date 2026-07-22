namespace TraderAi.Models;

public sealed class ShareEmissionRecipient
{
    public int Id { get; set; }

    public int ShareEmissionId { get; set; }

    public int ParticipantId { get; set; }

    public int Quantity { get; set; }

    public ShareEmission? ShareEmission { get; set; }
}
