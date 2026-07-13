# Share Price Formation

Share price is the latest recorded price point for a company. The market does not store a separate live quote that is continuously recalculated from all orders. A company's displayed price changes only when the simulation records a new price point.

## Cycle Flow

Each market cycle updates prices through a fixed sequence:

1. Resting orders are maintained before new decisions are made.
2. Corporate actions and forced-sale services can adjust the order book.
3. Automated traders and funds place new orders from the latest available prices.
4. The matcher fills crossing buy and sell orders.
5. Scheduled payouts and market events can add further price moves.
6. The cycle closes and the next cycle begins.

The complete sequence is one atomic database operation. If any phase fails, every change from the cycle is rolled back and the previous cycle remains current.

If no trade, split, or impact event touches a company during a cycle, that company keeps its previous price.

## Initial Price

When a demo market is seeded, each company receives an initial price and issued share supply. The full unsold float is listed as a company-originated sell order at that initial price, and the same price is recorded as the first price point.

Company-originated float has no participant seller. When a trader buys from that float, the buyer receives shares and pays cash, no trader receives the proceeds, and the issuer receives the primary proceeds on T+1 settlement.

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

This means the market does not calculate one global clearing price from total demand and supply. It forms price one fill at a time through price-time priority.

## Order Prices

Automated discretionary traders use the latest company price as their reference quote, biasing buys above and sells below it so ordinary orders have a chance to cross. The exact target, side, and quantity depend on recent price movement, long-range movement, order-book imbalance, available cash, holdings, debt pressure, temperament, and risk profile.

Every participant order — the player's and an automated one — must rest inside the allowed order range around the LULD reference, and a price beyond it is rejected. Continuous matching still only crosses orders inside the narrower executable band, so an order in the allowed range but outside the band waits until the band reaches it. See [LULD price controls](luld.md).

Most automated discretionary orders are priced inside the executable band; roughly one in ten instead rests in one of the two waiting segments just outside it, on either side, so some interest sits ahead of the band. Both buys and sells may use either waiting segment. Forced orders that must execute — margin-call, bankruptcy, loan-distress, and fund cash-raising sells — are pulled onto the nearest band edge, and issuer float rests at the listing reference, so none of them deliberately wait outside the band.

Resting automated orders can also move toward the market before matching, always clamped into the executable band so a stale order never compounds past it:

- A sell steps toward the band to cross, or climbs toward it when it rests below.
- A buy steps up toward the band, or down toward it when it rests above, reserving or releasing the cash difference.
- Very old automated orders can be cancelled, and any participant order left beyond the allowed range after the band moves is cancelled with its reservation released.

The human player's orders are not automatically aged or re-priced by ordinary maintenance, but they remain subject to the universal allowed-range validity check.

LULD price controls preserve participant and issuer orders. Persistent pressure at a price band pauses continuous matching through Limit State and Trading Pause, then eligible resting orders can execute at one deterministic reopening-auction price before normal matching resumes. See [LULD price controls](luld.md).

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

### Auditor Findings

Auditors review companies during the pre-match window. A severe finding can directly drop a company's price and trigger buyer order revisions before that cycle's automated decisions and matching.

### Stock Splits

When a company's per-share price grows too high, a stock split can re-denominate the shares. The split increases share counts and lowers the per-share price proportionally, preserving each holder's total value and the company's total market value.

The split-adjusted price is recorded as a new price point. The unsold float is re-denominated in place, while participant orders for the split company are cancelled so trading can restart around the adjusted price.

## Actions That Do Not Directly Set Price

Free-share emission does not directly write a new market price. It increases issued supply and grants new shares to eligible traders at zero cost, then lets normal trading absorb the added supply.

Dividends also do not directly set price. They transfer available issuer cash to shareholders when a payout window is due. That extra participant cash can influence later orders, but no price point is written by the dividend itself.

T+1 settlement does not defer price formation. A fill records its price point and changes economic ownership on the trade date; only the settled cash and share quantities wait until the next trading day.

Forced liquidation, bankruptcy sell-downs, and fund unwind sales do not directly set price either. They place sell orders at discounted limits clamped into the executable band so they can cross; price changes only if those orders match.

## Current Price And Charts

The current price shown for a company is the newest live price point recorded for that company. Price charts show those recorded points in order.

Because a price point is created only by a trade or a direct price event, quiet cycles do not produce flat duplicate points for every company. A company with no activity simply carries forward its latest price until a new point is recorded.

Historical retention moves older price points out of the live window but always keeps the newest point for each company as its current-price anchor. A quiet company therefore remains valued even when its last price is older than the normal chart window.
