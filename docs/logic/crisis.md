# Market crises

A crisis is a negative market event that shocks several industries at once and leaves the market in a temporary risk-off state. The initial price move happens when the crisis begins; its wider behavioral effects remain active for a limited number of later market cycles.

## When a crisis can begin

The market checks for a crisis on the first trading cycle of each trading day. Local and global crises have independent quiet-period clocks and probabilities:

- A local crisis cannot occur until ten trading days have elapsed since the previous local crisis. After that, its chance increases by three percentage points per trading day.
- A global crisis cannot occur until forty trading days have elapsed since the previous global crisis. After that, its chance increases by one percentage point per trading day.

Each scope resets only its own clock when it fires. The global check runs first, so a local crisis cannot begin in the same cycle as a newly triggered global crisis. If crises of different scopes overlap, the most recently triggered crisis owns consequences recorded during the overlapping window.

## Initial market shock

A local crisis affects one to three industries and remains active for five to fifteen cycles. A global crisis affects roughly 30–70% of all industries and remains active for fifteen to twenty-five cycles.

Each selected industry receives its own price decrease between 5% and 15%. The decrease is applied to every active company in that industry and creates a new price point. Because the move is downward, standing buy orders for affected companies are cancelled and their reserved cash is released. Player orders are exempt from this event-driven cancellation.

The trigger creates one industry-shock entry per affected industry on the crisis timeline. The crisis is considered active after its trigger cycle through the end of its duration window; the trigger cycle itself contains the immediate shock but is not part of the later active interval.

## Behavior during the active interval

An active crisis changes risk-sensitive behavior without replacing the ordinary market cycle:

- Sector sentiment becomes risk-off across the market, including industries outside the initial shock.
- Conservative and low-risk automated traders become less likely to place buy orders. The two traits stack when a trader has both.
- Bankruptcy probability is doubled for otherwise eligible traders.
- Audits have no crisis-specific multiplier; crisis-driven sector sentiment can still affect their recorded industry evidence.
- Departures caused by starvation or fund losses become twice as likely during a local crisis and five times as likely during a global crisis.
- Positive automated news impacts are less likely, while negative news remains eligible.
- Science investigations remain possible but occur at half their normal probability.

The crisis does not pause trading, suspend settlement, or directly rewrite participant balances and holdings beyond the effects produced by ordinary price, order, lifecycle, and accounting services.

## Crisis timeline

The crisis detail view combines the initial industry shocks with significant consequences that occur while the interval is active. It records:

- Industry shocks at the trigger cycle.
- High or Extra auditor ratings.
- Trader bankruptcies.
- Company closures.
- Collective-fund closures.

Ordinary trades, news posts, sentiment revisions, and participant departures remain visible in their normal histories rather than being copied into the crisis timeline.

The Crises roster shows every crisis newest first, including its scope, affected industries, price-impact range, event count, and trigger cycle. Opening a crisis shows its shocked industries and chronological consequence timeline. A current or recent crisis is also surfaced in the dashboard banner and Newswire.

## Related rules

- [Sector sentiment](sector-sentiment.md) describes the risk-on and risk-off state affected by a crisis.
- [Share price formation](../rules/share-price-formation.md) describes how direct event impacts and later trading establish prices.
- [Participant rules](../participant-rules.md) links to the lifecycle and trading rules for each participant role.
- [Trading days](../rules/trading-days.md) defines the daily boundary used by crisis probability checks.
