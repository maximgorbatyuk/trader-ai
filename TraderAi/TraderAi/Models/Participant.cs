using System.ComponentModel.DataAnnotations.Schema;

namespace TraderAi.Models;

public sealed class Participant
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public ParticipantType Type { get; set; }

    public decimal InitialBalance { get; set; }

    public decimal CurrentBalance { get; set; }

    public decimal ReservedBalance { get; set; }

    public Temperament Temperament { get; set; }

    public RiskProfile RiskProfile { get; set; }

    public bool IsActive { get; set; }

    // Consecutive cycles the trader wanted to buy but could not afford a single share anywhere; once it
    // reaches the liquidation threshold the trader sells down its priciest holding to free up cash.
    public int CashStarvedCycles { get; set; }

    [NotMapped]
    public decimal AvailableBalance => CurrentBalance - ReservedBalance;
}
