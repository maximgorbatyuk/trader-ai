# LULD Price Controls

Trader AI uses a deterministic Limit Up-Limit Down style state machine to prevent one company from continuously trading outside a rolling price band. It models the core price-band, pause, and reopening mechanics, not the complete venue and regulatory infrastructure of a real exchange.

## Reference price and bands

- The reference price is the arithmetic average of executed trade prices in the previous five minutes of active trading cycles, including the current cycle's position in that window.
- When no trade exists in the window, the latest company price snapshot is the fallback reference.
- The lower and upper bands default to 5% below and above the reference price and are rounded to cents.
- Ordinary continuous matching considers only orders whose limit is inside the current band. Outside-band orders remain open and cancellable.
- Direct price impacts such as news and crises are clamped to the active band. While a company is not in the normal state, stale-order cancellation is suppressed so its resting book is preserved.

## State sequence

| State | Trigger and duration | Matching behavior |
| --- | --- | --- |
| Normal | Default state. | Continuous matching runs for inside-band orders. |
| Limit State | Crossing pressure reaches the upper or lower band. It must persist for 15 active trading seconds, rounded to eight two-second cycles. | New order entry is disabled; resting orders remain open and cancellable; matching pauses. |
| Trading Pause | The same-side pressure survives the limit-state window. The pause lasts five minutes, or 150 active trading cycles. | New order entry and continuous matching remain disabled; resting orders are preserved. |
| Reopening | Begins after the pause expires. | One deterministic auction may execute eligible resting orders at one clearing price; normal trading resumes afterward. |

If pressure disappears or changes direction during Limit State, the company returns to Normal without a trading pause. Market-wide breaks and a manually paused market do not advance the security-specific duration counters because no trading cycle advances.

## Reopening auction

Only inside-band resting orders participate. The clearing price is chosen from their limit prices by:

1. highest executable quantity;
2. smallest buy-sell imbalance;
3. shortest distance from the reference price;
4. lower price as the final deterministic tie-breaker.

Eligible orders then execute at that single price with the normal price-time priority inside each side. Any unfilled quantity remains open after the company returns to Normal.

## Where to see it

- The top navigation shows **LULD: N affected** whenever at least one company is outside Normal.
- A company detail page shows **Price control**, **Price band**, and a text banner for **Limit State**, **Trading Pause**, or **Reopening**. A trading pause also shows the remaining active trading seconds.
- Company order books mark limits beyond the band as **Outside band** and show the current state with a text label and glyph.
- Order forms explain why entry is disabled during Limit State, Trading Pause, and Reopening. Existing orders remain available for cancellation.
- Entering Trading Pause publishes a company-specific Newswire item.

This mechanism does not implement multiple venues, national best bid and offer routing, regulatory reporting, or exchange-wide circuit breakers.
