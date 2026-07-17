using System.ComponentModel.DataAnnotations.Schema;

namespace TraderAi.Models;

public sealed class Participant
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public ParticipantType Type { get; set; }

    public decimal InitialBalance { get; set; }

    public decimal CurrentBalance { get; set; }

    public decimal SettledCashBalance { get; set; }

    public decimal ReservedBalance { get; set; }

    public Temperament Temperament { get; set; }

    public RiskProfile RiskProfile { get; set; }

    // Operator-marked favourite, surfaced on the dashboard's Favorite traders tab. Independent of any trading rule.
    public bool IsFavorite { get; set; }

    public bool IsActive { get; set; }

    // Consecutive cycles the trader wanted to buy but could not afford a single share anywhere; once it
    // reaches the liquidation threshold the trader sells down its priciest holding to free up cash.
    public int CashStarvedCycles { get; set; }

    // Set while the trader is in forced liquidation after going bankrupt; such a trader is skipped by the
    // decision engine and generic order ageing because the bankruptcy service owns its order lifecycle.
    public bool IsBankrupt { get; set; }

    // Consecutive cycles the trader's share holdings have stayed valued above the wealth line; the bankruptcy
    // chance ramps with it and the counter resets to zero once that value falls back below the line.
    public int WealthyCycles { get; set; }

    // Shares the trader owned when bankruptcy struck, the baseline the sell-down target is measured from.
    public int BankruptcyOwnedAtStart { get; set; }

    // How many times a forced-sale order has gone unsold and been re-listed; each step deepens the discount.
    public int BankruptcyDiscountStep { get; set; }

    // Consecutive cycles the participant could not afford any share; a long drought raises its odds of pooling
    // into a collective fund. Unlike CashStarvedCycles it is not reset by the liquidation pass, so it can grow.
    public int CannotBuyCycles { get; set; }

    // The cycle the trader entered the market; 0 for rows seeded before this tracking existed. Copied onto the
    // MarketExit archive so a departed trader's tenure is still knowable after its row is deleted.
    public int JoinedInCycleId { get; set; }

    // High-water mark of cash plus holdings value, ratcheted up each cycle and never lowered. Archived on exit
    // to show how far a trader climbed before leaving broke.
    public decimal MaxTotalWorth { get; set; }

    // Set when a collective fund closes and hands this member a payout that is a fraction of its deposit; the
    // market-exit service then gives the member one chance to quit on its first shareless cycle.
    public bool PendingFundLossExitRoll { get; set; }

    // Set when a member leaves its fund to chase a better-performing one; the fund's join pass then places it in
    // the best available fund regardless of the usual cash ceiling and clears the flag.
    public bool PendingFundSwitch { get; set; }

    // Behavioural-audit scores, refreshed every thirtieth cycle from the trader's recent order and trade activity.
    // Temperament sums all five normalised activity metrics; risk sums the three that read as risk appetite. Both
    // seed the nearest-group-average reclassification of the Player and Player's Fund.
    public decimal TemperamentIndex { get; set; }

    public decimal RiskProfileIndex { get; set; }

    [NotMapped]
    public decimal AvailableBalance => CurrentBalance - ReservedBalance;
}
