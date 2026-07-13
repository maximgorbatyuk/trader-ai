using System.ComponentModel.DataAnnotations.Schema;

namespace TraderAi.Models;

public sealed class MarginAccount
{
    public int Id { get; set; }
    public int ParticipantId { get; set; }
    public decimal DebitBalance { get; set; }
    public decimal AccruedInterest { get; set; }
    public decimal InitialMarginRate { get; set; }
    public decimal MaintenanceMarginRate { get; set; }
    public MarginAccountStatus Status { get; set; }
    public int? LastInterestAccruedTradingDayId { get; set; }

    [NotMapped]
    public decimal TotalLiability => DebitBalance + AccruedInterest;
}
