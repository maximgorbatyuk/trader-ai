# Company

A Company is the listed asset in the game. It issues shares, belongs to an industry, has a market price, and can be affected by trades and market events. It is not an automated trader.

## Rules

- A Company starts with an issued share supply and an initial market price.
- Unsold issued shares are available through the company's float. When those shares sell, the buyer receives shares, no participant seller receives cash, and the primary proceeds enter the company's issuer cash on T+1 settlement.
- When unsold float is below the configured scarcity threshold, the Company can create a deterministic, demand-paced replenishment offer at the current price. The offer requires executable unmet buy demand from active, non-bankrupt Individuals or AI Agents after compatible resting sell supply has been subtracted; Player and Collective Fund demand does not trigger it. The canonical trigger, cap, and matching rules are in [Share price formation](../rules/share-price-formation.md).
- A stale replenishment offer outside the active price band is cancelled without changing the initial float offer. The next demand-paced offer uses already issued but unlisted float before issuing additional shares.
- A Company can create at most one replenishment offer per trading day. Newly issued quantity is capped at the smaller of residual unmet demand and 25% of issued shares rounded up, and the offer enters the ordinary order book. Replenishment is deferred while the security is in Limit State, Trading Pause, or Reopening.
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
- During the market's first five trading days, lifecycle rules neither delist companies nor apply the protected-company price reduction described below. Those delisting and price-reduction checks begin on Day 6.
- A newly listed company starts, like any company, with a random share supply, a random starting price, an industry, and a name, and its listing is announced on the Newswire.
- Every newly listed company has its own five-trading-day lifecycle safe period, independent of the market-wide first-five-day period. A company listed on Day N is protected through Day N+4 and becomes eligible for lifecycle action on Day N+5.
- During this safe period, the company is excluded from delisting for recent declines or poor ratings and from forced delisting when the market is at maximum size. It also cannot receive the protected-company price reduction.
- The safe period applies only to lifecycle actions. Trades and other market events can still move the company's price.
- After a company lists, there is a quiet stretch during which no new company can appear; past it, the chance of a new listing rises a little each cycle until one appears, which restarts the quiet stretch.
- A company is delisted when its price has fallen in most of the recent cycles, or when its most recent risk ratings are poor several times in a row. At most one company is delisted per cycle.
- A failing company that represents at least 0.5% of total live-market capitalization is protected from delisting. The market instead requests a 60% price reduction; LULD can clamp the realized move, and eligible ordinary buy orders are cancelled through the usual downward-impact behavior. See [Share price formation](../rules/share-price-formation.md).
- When the market is already at its maximum size and nothing has failed on its own, the worst-performing company is delisted to make room for new listings.
- A company that recently received a big investment is shielded from delisting for the next five trading days, like a freshly listed company. See [Big investment](../logic/big-investment.md).
- Delisting cancels the company's open orders and wipes out its shares — shareholders recover nothing — and the delisting is announced on the Newswire.
- A delisted company drops off the live company list and the market map but keeps its history; it is listed on the Closed Companies page and its detail page still opens, marked as delisted.
