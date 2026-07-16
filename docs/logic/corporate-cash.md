# Corporate Cash

Each listed company has its own issuer cash balance and an append-only movement ledger. Corporate cash is separate from participant cash, bank revenue, and market capitalization. Simulated operating income is an explicit external-economy cash source rather than money transferred from another account inside the market.

## Cash sources

- A newly seeded or newly listed company starts with zero issuer cash.
- Selling the company's unsold float is primary issuance. The buyer pays on the trade date, but the issuer receives the proceeds when the trade settles on T+1.
- If issuer float is strictly below 10% of issued shares, deterministic demand-paced issuance can add a block at the current price. It triggers only for unmet executable Individual and AI Agent buy demand after compatible resting sell supply is shadow-matched with normal price-time priority and self-cross protection; Player and Collective Fund demand does not trigger it.
- Issuance happens at most once per company per trading day. Its quantity is the smaller of unmet demand and 25% of issued shares rounded up, and it is deferred while the security is in Limit State, Trading Pause, or Reopening. The issuer order then uses ordinary matching and T+1 settlement.
- A secondary-market trade transfers value between participants and does not change issuer cash.
- A big investment credits the company immediately when a participant or fund buys a block of newly minted shares at the current price. See [Big investment](big-investment.md).
- Operating income represents value earned from goods and services outside the accounts modeled by the simulation. It adds cash to the issuer without debiting a participant, bank, or customer account.

## Operating-income and dividend window

- The market schedules a shared corporate-cash window after a random 10–25 trading cycles, then chooses a new interval after each window. The one-minute trading break does not advance this schedule because it does not create trading cycles.
- For each active company with a current price, operating income and dividends use independent event rolls. A company can receive income, declare a dividend, do both, or do neither in the same window.
- Both rolls use the same capitalization-stability classification. With the default configuration, an event has a 75% chance when capitalization is within 5% of its previous-window baseline and a 25% chance after a larger move.
- When an event succeeds, both income and dividend calculations draw from the same configured rate band, 0.03%–1.5% by default.
- Operating income uses the company's full issued capitalization, including unsold float:

  `operating income = min(round-to-cents(current price × issued shares × rate), $1,000,000)`

- A rounded zero amount creates no cash or ledger movement. Closed companies and companies without a current price receive no operating income.
- Positive operating income is credited before dividends are funded in the same window. A company that began the window with no cash can therefore fund a dividend from income earned in that window.

## Dividend funding

- A dividend is funded from the issuer's available cash. The payable pool is capped by both the declared amount and the current issuer cash balance.
- If available issuer cash is lower than the declared dividend, the payout is reduced. If there is no issuer cash, the payout is skipped. The Newswire reports either outcome.
- A funded dividend is allocated proportionally to eligible holdings. Amounts are rounded to cents with deterministic remainder allocation, so participant credits exactly equal the issuer debit.
- Unsold float receives no dividend. The declared pool uses participant-owned shares and is capped at $1,000,000 per company per window.
- Corporate cash cannot become negative through dividend payment.

## Ledger and reconciliation

Every primary-issuance credit, operating-income credit, funded dividend debit, and closure distribution is recorded as a positive amount with a movement type that determines its direction. From a company's creation, its balance reconciles as:

`issuer cash = primary issuance + big investment + operating income − funded dividend declarations − closure distributions`

Operating income increases the amount of cash in the simulated system. System-wide cash conservation therefore treats cumulative operating-income movements as a declared external source instead of hiding them as an unexplained balance mutation. A closure distribution is an internal issuer outflow, not an external source. Market capitalization remains a valuation and is never itself spendable cash.

This is intentionally a simplified company model. It does not model expenses, taxes, retained earnings, customer accounts, or separate goods and services markets.

## Where to see it

- A company detail page shows **Issuer cash** in the headline statistics.
- The **Corporate cash movements** panel lists each movement with its type, signed amount, direction, and cycle. Primary sales appear as **Primary issuance** credits, big-investment deals as **Big investment** credits, external earnings as **Operating income** credits, and funded payouts as **Dividend paid** debits.
- Reduced or skipped dividends appear as company-specific items in the **Newswire**.
- Secondary trades remain visible in trade history but do not add rows to the corporate cash panel.

See [Trade settlement](settlement.md) for the timing of primary proceeds and [Company](../roles/company.md) for issuer behavior.
