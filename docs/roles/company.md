# Company

A Company is the listed asset in the game. It issues shares, belongs to an industry, has a market price, and can be affected by trades and market events. It is not an automated trader.

## Rules

- A Company starts with an issued share supply and an initial market price.
- Unsold issued shares are available through the company's float. When those shares sell, the buyer receives shares, but no participant seller receives cash.
- A Company does not make automated trading decisions, join funds, go bankrupt, or leave the market as trader churn.
- Shareholders can be Individuals, AI Agents, the Player, or Collective Funds.
- Company price changes through matched trades and market events.
- News can move one company or every company in selected industries up or down.
- A crisis moves affected industries down and can cancel ordinary buy orders priced against the old level.
- A science investigation moves affected industries up without cancelling orders.
- A Company can pay dividends during scheduled payout windows. Dividends go to current share owners, not to unsold float.
- Dividend likelihood is higher when the company's market value has stayed stable and lower after sharp swings.
- When the share price grows too high, a stock split can increase share counts and lower the per-share price without changing holder value or total market value.
- During a split, the unsold float is re-denominated and participant orders for that company are cancelled so trading can restart around the adjusted price.
