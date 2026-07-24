# Share Price Formation

Share price is the latest recorded price point for a company. The market does not store a separate live quote that is continuously recalculated from all orders. A company's displayed price changes only when the simulation records a new price point.

## Cycle Flow

Each market cycle updates prices through a fixed sequence:

1. Resting orders are maintained before new decisions are made.
2. Corporate actions can adjust supply, share denomination, and the order book.
3. At the opening and midpoint checkpoints, companies persist updated financial evidence.
4. Lifecycle, forced-sale, exit, and concentration services run; at the opening of an audit-effective day, auditors persist completed-window evidence.
5. Automated traders and funds combine the latest effective evidence with current market state and place new orders.
6. The matcher fills crossing buy and sell orders.
7. Scheduled payouts and market events can add further price moves.
8. The cycle closes and the next cycle begins.

The complete sequence is one atomic database operation. If any phase fails, every change from the cycle is rolled back and the previous cycle remains current.

If no trade, split, or impact event touches a company during a cycle, that company keeps its previous price.

## Initial Price

When a demo market is seeded, each company receives an initial price and issued share supply. The full unsold float is listed as a company-originated sell order at that initial price, and the same price is recorded as the first price point.

Company-originated float has no participant seller. When a trader buys from that float, the buyer receives shares and pays cash, no trader receives the proceeds, and the issuer receives the primary proceeds on T+1 settlement.

When unsold issuer float is below the configured scarcity threshold — 20% of issued shares by default — the company can answer executable unmet buy demand from active, non-bankrupt Individuals and AI Agents. Before issuing, it shadow-matches compatible resting sell supply with the same price-time and self-cross rules as normal matching; Player and Collective Fund demand does not trigger new supply. The company creates at most one demand-paced replenishment offer per trading day, and newly issued quantity is capped at the smaller of remaining demand and 25% of issued shares rounded up.

An open replenishment offer that moves outside the active price band is cancelled without touching the company's initial float offer. When eligible unmet demand next appears, the replacement offer is created at the exact current price and uses already issued but unlisted float before expanding the issued share count. The offer uses ordinary matching, so absorbed participant cash reaches issuer cash through normal T+1 settlement. Replenishment is deferred while the company is in Limit State, Trading Pause, or Reopening.

## Matching Price

Normal price formation happens through matched orders.

For each company in normal continuous trading, open inside-band buy orders are matched against open inside-band sell orders:

- Buy orders have priority by higher limit price, then older order time.
- Sell orders have priority by lower limit price, then older order time.
- A match happens only when the best buy limit is greater than or equal to the best sell limit.
- The matched quantity is the smaller remaining quantity of the two orders.
- The execution price is the midpoint between the matched buy and sell limits, rounded to cents.
- Orders owned by the same participant never match. New opposite-side interest is rejected before reservation, and a legacy self-cross is cancelled without a fill or price point.
- Closed companies reject new orders and do not enter matching.
- Orders whose limits are outside the active LULD band remain open and cancellable but do not enter continuous matching.

Formula:

```text
execution price = round((buy limit + sell limit) / 2, 2)
```

Each fill records a new price point at its execution price. If several fills happen for the same company in one cycle, the last recorded fill price becomes the current displayed price.

This midpoint rule is an explicit game-specific exchange difference. A conventional continuous limit-order book normally executes a crossing incoming order at the resting order's price; this simulation deliberately splits the spread between both submitted limits. Price-time priority still decides which orders meet, but it does not decide the execution price.

The market does not calculate one global clearing price from total demand and supply. It forms price one fill at a time through priority matching and the midpoint rule.

## Directional signals and action probabilities

Before a rule-based Individual or Collective Fund chooses an order, every tradable company receives a directional score from -1 to +1. Five independently normalized components contribute:

- **Momentum** compares the recent price move with the active LULD range.
- **Order flow** compares executable buy and sell interest.
- **Industry** normalizes current sector sentiment.
- **Audit** maps Extra raised expectations and Raised expectations to strong and moderate positive evidence, Stable to neutral, and Low risk and High risk to moderate and strong negative evidence.
- **Fundamentals** combine profitability, stability, closure safety, dividend coverage, management outlook, and forecast growth. Low-risk profiles respond more strongly to quality; high-risk profiles respond more strongly to growth; medium-risk profiles balance them.

The weighted sum is clamped to the same -1 to +1 range. The strongest eligible positive company becomes the buy target, while the weakest held company becomes the sell target. Positive target evidence adds buy weight and negative target evidence adds sell weight. A separate wait weight grows when evidence is weak, while temperament adds bounded activity noise. Exposure, bargain buying, profit-taking, debt, crisis behavior, passive-supply chance, and hard order eligibility can adjust or block an action.

The final non-negative buy, sell, and wait weights are normalized:

```text
buy probability  = buy weight  / total weight
sell probability = sell weight / total weight
wait probability = wait weight / total weight
```

One random draw selects the action. The positive minimum wait weight and personality contribution mean good evidence raises the chance of buying and bad evidence raises the chance of selling without making either certain. The evidence helps predict aggregate pressure within a trading day, not an individual order or guaranteed price direction.

AI Agents receive the same raw evidence and normalized components but choose their own explicit predictions and orders. The human player sees the reports and decides independently.

## Order Prices

After the action and target are selected, a rule-based Individual buy uses its risk-specific exposure headroom. It crosses the target's best residual in-band ask at that exact ask price. When a company has no remaining open sell interest and its price is rising or stable over recent cycles, an eligible Individual can instead create a small passive bid above the current market price after a configurable chance succeeds; temperament selects progressively higher thirds of the configured premium range. A company whose price has fallen over that window receives no such bid, so above-market demand concentrates on healthy names. The limit remains inside the executable band, and a sell that exists outside that band prevents the company from being treated as having no supply. Orders generated earlier in the same decision batch retain their price-time shadow, so a later buy cannot jump demand that has already been allocated. When a participant decides to buy, it places a configurable random number of buy orders across distinct eligible companies rather than a single order, so a buying cycle can build broader demand.

Configured AI Agents receive the same live exposure and execution envelope but choose their own side, company, signed price offset, quantity, and reason. When earlier demand has priority over lower-priced supply, the envelope can offer a passive bid at that priority ceiling instead of suggesting an unsafe crossing price. At application time the backend resolves the offset against the freshest market price and clamps the resulting limit onto the allowed band, then accepts or rejects the unchanged quantity under the current execution and exposure rules. An accepted limit is not subsequently re-priced by ordinary order maintenance.

Every participant order — the player's and an automated one — must rest inside the allowed order range around the LULD reference, and a price beyond it is rejected. Continuous matching still only crosses orders inside the narrower executable band, so an order in the allowed range but outside the band waits until the band reaches it. See [LULD price controls](luld.md).

Rule-based Individual buys stay inside the executable band. Ordinary passive limits produced for rule-based sells and both sides of Collective Fund trading use a centered random offset with equal opportunity to fall above or below the active reference, then clamp to the active band. This symmetry prevents the price generator itself from embedding a permanent buy-side or sell-side drift. The separate no-supply Individual bid described above is intentionally positive because it represents demand rather than a neutral passive quote. Forced orders that must execute — margin-call, bankruptcy, loan-distress, and fund cash-raising sells — are pulled onto the nearest band edge. Issuer float rests at its listing reference.

Resting rule-based automated and fund orders can also move toward the market before matching, always clamped into the executable band so a stale order never compounds past it:

- A sell steps toward the band to cross, or climbs toward it when it rests below.
- A buy steps up toward the band, or down toward it when it rests above, reserving or releasing the cash difference.
- Very old automated orders can be cancelled, and any participant order left beyond the allowed range after the band moves is cancelled with its reservation released.

Accepted AI Agent orders are not re-priced by this maintenance step: their exact buy or sell limit remains unchanged while they rest, and a buy reservation stays tied to that limit. They can still fill, be cancelled by the agent, expire at the automated age cap, or be cancelled by structural market rules such as an invalid allowed range or stock split.

The human player's orders are not automatically aged or re-priced by ordinary maintenance, but they remain subject to the universal allowed-range validity check.

LULD price controls preserve participant and issuer orders. Persistent pressure at a price band pauses continuous matching through Limit State and Trading Pause, then eligible resting orders can execute at one deterministic reopening-auction price before normal matching resumes. See [LULD price controls](luld.md).

Persistent unmatched demand can also move the LULD reference before it reaches the limit-state boundary. When in-band buy quantity exceeds sell quantity at the end of a matching cycle, the next cycle nudges the reference upward by a small configured step so later orders form around a higher level. The ratchet stops when the imbalance clears and does not change midpoint execution.

## Direct Price Moves

Some events record a new price point without a trade.

### News And Crises

News and crisis impacts move affected companies by a percentage of the current price:

```text
new price = current price * (1 + impact percent)
new price = current price * (1 - impact percent)
```

The result is clamped to the company's active LULD band. While the company is in Limit State, Trading Pause, or Reopening, the direct impact does not clear its preserved resting orders.

A downward shock can cancel ordinary standing buy orders for the affected companies. An upward shock can cancel ordinary standing sell orders. This lets the order book reform around the new price instead of immediately filling stale orders priced against the old level.

News aimed at a single company also ripples to the rest of that company's industry as a sympathy move: every other company in the same industry moves in the same direction by a quarter of the headline percentage.

```text
peer impact percent = company impact percent * 0.25
```

### Science Investigations

A science investigation is a positive sector event. It raises affected companies by a percentage of the current price, but does not clear the order book.

### Big Investments

A big-investment deal records the unchanged per-share deal price against the enlarged share supply. The attached raised-expectations rating then separately requests an 8% price increase. LULD can clamp the realized move, and this rating impact preserves resting orders rather than cancelling stale orders. See [Big investment](../logic/big-investment.md).

### Concentration Control

Concentration control compares each live company with total live-market capitalization. When a company exceeds the configured share — 20% by default — the market requests a 25% price reduction by default. The check can apply again in later cycles while the company remains too concentrated. LULD can clamp each realized move, and eligible ordinary buy orders are cancelled so stale demand does not immediately trade against the reduced price.

### Lifecycle Repricing

A company that meets lifecycle failure conditions but represents at least 0.5% of total live-market capitalization is protected from delisting. The market instead requests a 60% price reduction. LULD can clamp the realized move, and eligible ordinary buy orders are cancelled through the same downward-impact behavior used for other shocks. Company age, market age, and recent big-investment protection determine whether this lifecycle action is eligible; see [Company](../roles/company.md).

### Stock Splits And Reverse Splits

When a company's per-share price grows too high, a stock split can re-denominate the shares. The split increases share counts and lowers the per-share price proportionally, preserving each holder's total value and the company's total market value.

A reverse split applies the same rules in the other direction when a per-share price becomes too low. Whole-share division can discard a sub-share remainder, so holder value and capitalization can decrease by that small remainder.

The adjusted price is recorded as a new price point. The unsold float and current LULD reference and band are re-denominated in place, while participant orders are cancelled so trading can restart around the adjusted price. Earlier trades remain unchanged as historical facts but stop contributing to the rolling LULD reference; the first new order and trade therefore use only the new price scale.

## Actions That Do Not Directly Set Price

Demand-paced primary issuance does not directly write a new market price. It increases issued supply and lists the new float at the current price, then lets ordinary matching determine whether demand absorbs it.

Free-share emission also does not directly write a new market price. It increases issued supply and grants settled shares to eligible traders at zero cost without placing an executable zero-price order in the book. The larger supply and recipients' later sell decisions can influence price only through subsequent normal trading.

Dividends also do not directly set price. They transfer available issuer cash to shareholders when a payout window is due. That extra participant cash can influence later orders, but no price point is written by the dividend itself.

Company financial checkpoints and periodic audits do not directly set price. They alter the evidence available to traders for the effective day, which changes probabilistic demand and supply only when a participant subsequently chooses and places an order. Audit creation therefore does not clear resting orders. See [Company fundamentals](../logic/company-fundamentals.md) and [Auditors](../roles/auditors.md).

T+1 settlement does not defer price formation. A fill records its price point and changes economic ownership on the trade date; only the settled cash and share quantities wait until the next trading day.

Forced liquidation, bankruptcy sell-downs, and fund unwind sales do not directly set price either. They place sell orders at discounted limits clamped into the executable band so they can cross; price changes only if those orders match.

Short selling remains unavailable. Every sell order is capped by owned shares that are not already committed to another sell; there is no stock borrow or buy-to-cover flow.

## Current Price And Charts

The current price shown for a company is the newest live price point recorded for that company. Price charts show those recorded points in order.

Because a price point is created only by a trade or a direct price event, quiet cycles do not produce flat duplicate points for every company. A company with no activity simply carries forward its latest price until a new point is recorded.

Historical retention moves older price points out of the live window but always keeps the newest point for each company as its current-price anchor. A quiet company therefore remains valued even when its last price is older than the normal chart window.
