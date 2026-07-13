namespace TraderAi.Models;

public sealed class MarginCall
{
    public int Id { get; set; }
    public int MarginAccountId { get; set; }
    public int OpenedInTradingDayId { get; set; }
    public int OpenedInCycleId { get; set; }
    public int? ClosedInTradingDayId { get; set; }
    public decimal AccountEquity { get; set; }
    public decimal MaintenanceRequirement { get; set; }
    public decimal Deficiency { get; set; }
    public MarginCallStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}
