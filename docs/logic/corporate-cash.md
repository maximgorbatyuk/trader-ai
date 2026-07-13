# Corporate Cash

Each listed company has its own issuer cash balance and an append-only movement ledger. Corporate cash is separate from participant cash, bank revenue, and market capitalization.

## Rules

- A newly seeded or newly listed company starts with zero issuer cash.
- Selling the company's unsold float is primary issuance. The buyer pays on the trade date, but the issuer receives the proceeds when the trade settles on T+1.
- A secondary-market trade transfers value between participants and does not change issuer cash.
- A dividend is funded from the issuer's available cash. The payable pool is capped by both the declared amount and the current issuer cash balance.
- If available issuer cash is lower than the declared dividend, the payout is reduced. If there is no issuer cash, the payout is skipped. The Newswire reports either outcome.
- A funded dividend is allocated proportionally to eligible holdings. Amounts are rounded to cents with deterministic remainder allocation, so participant credits exactly equal the issuer debit.
- Every primary-issuance credit and dividend debit creates a corporate cash movement. This ledger makes the company's current cash balance reconcilable without treating market capitalization as spendable cash.
- Corporate cash cannot become negative through dividend payment.

## Where to see it

- A company detail page shows **Issuer cash** in the headline statistics.
- The **Corporate cash movements** panel lists each movement with its type, signed amount, and cycle. Primary sales appear as **Primary issuance** credits and funded payouts as **Dividend paid** debits.
- Reduced or skipped dividends appear as company-specific items in the **Newswire**.
- Secondary trades remain visible in trade history but do not add rows to the corporate cash panel.

See [Trade settlement](settlement.md) for the timing of primary proceeds and [Company](../roles/company.md) for issuer behavior.
