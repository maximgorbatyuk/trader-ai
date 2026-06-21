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

    // Set while the trader is in forced liquidation after going bankrupt; such a trader is skipped by the
    // decision engine and generic order ageing because the bankruptcy service owns its order lifecycle.
    public bool IsBankrupt { get; set; }

    // Consecutive cycles the trader's net worth has stayed above the wealth line; the bankruptcy chance
    // ramps with it and the counter resets to zero once net worth falls back below the line.
    public int WealthyCycles { get; set; }

    // Shares the trader owned when bankruptcy struck, the baseline the 80% sell-down target is measured from.
    public int BankruptcyOwnedAtStart { get; set; }

    // How many times a forced-sale order has gone unsold and been re-listed; each step deepens the discount.
    public int BankruptcyDiscountStep { get; set; }

    [NotMapped]
    public decimal AvailableBalance => CurrentBalance - ReservedBalance;
}
