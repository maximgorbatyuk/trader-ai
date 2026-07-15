# Company

A Company is the listed asset in the game. It issues shares, belongs to an industry, has a market price, and can be affected by trades and market events. It is not an automated trader.

## Rules

- A Company starts with an issued share supply and an initial market price.
- Unsold issued shares are available through the company's float. When those shares sell, the buyer receives shares, no participant seller receives cash, and the primary proceeds enter the company's issuer cash on T+1 settlement.
- When unsold float is strictly below 10% of issued shares, the Company can issue a deterministic, demand-paced block at the current price. Issuance requires executable unmet buy demand from active, non-bankrupt Individuals or AI Agents after compatible resting sell supply has been subtracted; Player and Collective Fund demand does not trigger it.
- A Company can issue at most once per trading day. The block is the smaller of unmet demand and 25% of issued shares rounded up, and it enters the ordinary order book. Issuance is deferred while the security is in Limit State, Trading Pause, or Reopening.
- A Company does not make automated trading decisions, join funds, or go bankrupt. It can be delisted, but through its own lifecycle rules rather than the trader-churn that removes traders (see Lifecycle).
- Shareholders can be Individuals, AI Agents, the Player, or Collective Funds.
- Company price changes through matched trades and market events.
- News can move one company or every company in selected industries up or down.
- A crisis moves affected industries down and can cancel ordinary buy orders priced against the old level.
- A science investigation moves affected industries up without cancelling orders.
- During each scheduled corporate-cash window, a Company independently tests for simulated operating income and a dividend. Income is credited before dividend funding; detailed timing, calculation, and accounting rules are in [Corporate cash](../logic/corporate-cash.md).
- Dividends go to current share owners, not to unsold float, and cannot exceed the company's available issuer cash.
- The company detail page shows **Issuer cash** and a **Corporate cash movements** ledger where simulated earnings appear as an **Operating income** credit alongside primary issuance and funded dividend payments.
- Dividend likelihood is higher when the company's market value has stayed stable and lower after sharp swings.
- When the share price grows too high, a stock split can increase share counts and lower the per-share price without changing holder value or total market value.
- During a split, the unsold float is re-denominated and participant orders for that company are cancelled so trading can restart around the adjusted price.
- LULD price controls can move the company through Limit State, Trading Pause, and Reopening without cancelling its resting book.

## Lifecycle

- The market's company roster is not fixed. New companies can list over time and failing companies can be delisted, up to a maximum number of live companies.
- A newly listed company starts, like any company, with a random share supply, a random starting price, an industry, and a name, and its listing is announced on the Newswire.
- After a company lists, there is a quiet stretch during which no new company can appear; past it, the chance of a new listing rises a little each cycle until one appears, which restarts the quiet stretch.
- A company is delisted when its price has fallen in most of the recent cycles, or when its most recent risk ratings are poor several times in a row. At most one company is delisted per cycle.
- When the market is already at its maximum size and nothing has failed on its own, the worst-performing company is delisted to make room for new listings.
- Delisting cancels the company's open orders and wipes out its shares — shareholders recover nothing — and the delisting is announced on the Newswire.
- A delisted company drops off the live company list and the market map but keeps its history; it is listed on the Closed Companies page and its detail page still opens, marked as delisted.
