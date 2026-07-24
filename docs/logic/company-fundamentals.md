# Company Fundamentals

Every listed company has a forward-only financial record that is separate from its market price and issuer cash ledger. The record gives the player, rule-based traders, AI Agents, funds, and auditors common evidence for judging company quality, growth, volatility, and future closure risk.

## Seed baseline

The first financial snapshot is created when a company is listed. Its values are randomized within bounded, mutually consistent ranges tied to the company's listing capitalization:

- assets anchor the scale of the balance sheet;
- revenue is derived from assets, while profit and operating cash flow are derived from revenue and profit;
- liabilities remain bounded relative to assets, and debt cannot exceed liabilities;
- expected dividends remain affordable from positive profit and operating cash flow;
- management forecasts stay within a bounded deviation from current operating results;
- business risk and the direction of later changes are nudged by whether the industry is rising, on a plateau, or falling.

This produces varied companies without seeding implausible combinations such as debt above liabilities or dividends unsupported by both profit and cash flow. Newly listed companies use the same baseline process; older history is never fabricated for them.

## Reporting checkpoints

The simulation records at most one immutable financial snapshot for each company at each of these moments:

- **Seed** — the listing baseline.
- **Day opening** — the first trading cycle of a day.
- **Midday** — the first cycle of the second half of the trading day.

At opening and midday, every raw metric receives an independent chance to remain unchanged or move by a bounded random magnitude. A shared company impulse makes changes directionally related without forcing every metric to move together. Revenue, profit, cash flow, assets, dividends, forecasts, and confidence treat an increase as favorable; liabilities, debt, and business risk treat a decrease as favorable. Industry direction biases that impulse toward favorable changes in rising sectors and unfavorable changes in falling sectors.

After every checkpoint, balance-sheet, forecast, and dividend invariants are restored before the snapshot is scored and stored. The snapshot also records which raw metrics changed.

## Raw financial indicators

The current report and its history contain:

- revenue and net profit;
- reported operating cash flow;
- total assets, total liabilities, and total debt;
- expected dividend per share, expected dividend pool, and dividend coverage;
- business risk;
- management forecasts for revenue, profit, and operating cash flow;
- management confidence and the resulting Positive, Neutral, or Negative outlook;
- the most recent actual dividend outcome when one exists.

Reported operating cash flow is an analytical company-performance measure. It is not the issuer's spendable cash balance and does not itself create a cash-ledger entry. The separate [Corporate cash](corporate-cash.md) page explains primary proceeds, external operating-income credits, and actual dividend funding.

## Derived indicators

Each checkpoint derives three scores from 0 to 100 and classifies them as Low, Medium, or High:

- **Profitability** combines net margin, return on assets, operating-cash-flow quality, recent revenue and profit direction, and management outlook.
- **Stability** measures the average magnitude of recent changes across the operating, balance-sheet, dividend, risk, forecast, and confidence metrics. The displayed volatility level is the inverse classification: high stability means low volatility.
- **Closure risk** combines negative profit and cash-flow streaks, leverage, liabilities relative to assets, current and changing business risk, management outlook, and industry direction.

Management outlook compares current values with management forecasts and scales their direction by confidence. Mixed positive and negative forecasts remain Neutral.

The same financial evidence is interpreted differently by automated risk profiles. Low-risk traders give more weight to quality, stability, dividend coverage, and closure safety; high-risk traders give more weight to forecast growth and confident guidance; medium-risk traders balance both. This changes their response, not the underlying company score. See [Participant rules](../participant-rules.md).

## History and presentation

Financial snapshots are persisted in checkpoint order and are never rewritten by later reports. The company page separates the current **Financials**, **Management outlook**, and **Financial history** tabs. History is loaded only when selected, is paged newest first, and shows the current value, previous value, absolute change, and percentage change for the chosen metric.

Missing history is reported as unavailable rather than replaced with zeroes. Closed-company pages retain their last recorded financial evidence.

## Consumers

Fundamentals are evidence, not direct price commands:

- [Auditors](../roles/auditors.md) include the latest completed-window financial snapshot when assigning the next trading day's status.
- Rule-based Individuals and Collective Funds combine fundamentals with momentum, order flow, industry direction, and the effective audit to form a bounded directional signal.
- AI Agents receive the raw snapshot, derived scores, deltas, audit evidence, and the same normalized signal components, but still choose their own orders.
- The player can compare the current report with its history and decide independently.

No financial checkpoint writes a market price or closes a company automatically. Prices still form through the mechanisms in [Share price formation](../rules/share-price-formation.md), while delisting remains governed by the company's lifecycle rules.
