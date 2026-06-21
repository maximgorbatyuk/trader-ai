namespace TraderAi.Models;

// A trader's collapse: once a participant's net worth stays above the wealth line its bankruptcy chance
// ramps each cycle, and when it fires the trader's cash is wiped and most of its holdings are dumped. The
// record keeps the headline figures so the event can be shown in the newswire and on the trader's page.
public sealed class Bankruptcy
{
    public int Id { get; set; }

    public int ParticipantId { get; set; }

    public required string Title { get; set; }

    public required string Content { get; set; }

    // Cash wiped from the balance at the moment of bankruptcy; later liquidation proceeds are not counted here.
    public decimal CashLost { get; set; }

    // Market value of the shares the trader still held when bankruptcy struck.
    public decimal ShareWorth { get; set; }

    public int TriggeredInCycleId { get; set; }

    public DateTime TriggeredAt { get; set; }
}
