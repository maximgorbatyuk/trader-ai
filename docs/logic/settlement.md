# Trade Settlement

An executed trade changes the participant's economic cash and share position immediately, while its cash and securities delivery completes on the next trading day. This T+1 distinction makes pending obligations visible without delaying price formation or ordinary portfolio decisions.

## Rules

- Every fill creates one pending settlement instruction for its buyer, seller when one exists, company, quantity, cash amount, trade day, and due day.
- The default settlement lag is one trading day: a trade on Day 4 is due on Day 5. The one-minute market break does not count as a trading day.
- Economic balances and holdings change on the trade date. The corresponding settled cash and settled share quantities change when the due trading day opens and the instruction is settled.
- A participant may sell shares bought earlier on the same day. Settlement applies all due instructions in order and nets the resulting cash and quantity changes, so a same-day buy and resale do not require a temporary settled position.
- Primary issuance uses the same lag. The buyer's settled cash and shares move on T+1, and the issuer receives corporate cash on that settlement date.
- Margin advances and sale-proceeds repayment are recorded on the settlement instruction so economic and settled cash remain reconcilable.
- Cash moved into or out of a collective fund must already be settled and unreserved. Pending sale proceeds become transferable only after settlement.
- Settlement cannot create a negative settled share position. A missing participant, company, or holding causes the cycle transaction to fail instead of silently losing an obligation.
- The current model has no failed-delivery workflow, clearing member, or settlement penalty. Short selling and stock borrow are planned for later and are not implemented.

## Realized sale performance

Every new fill with a participant seller records the seller's weighted average cost immediately before the holding is reduced. The fill's cost basis is that average cost multiplied by the sold quantity.

- Gross realized profit or loss is sale proceeds minus seller cost basis.
- Net realized profit or loss is gross realized profit or loss minus the seller's direct trade fee and any collective-fund manager fee charged on that sale.
- Margin-debit repayment, margin interest, loan interest, and other financing costs remain separate cash or liability evidence. They are not included in trade-level net realized profit or loss.
- Issuer sales have no participant seller basis or realized profit fields.
- Older fills remain without these values when the exact at-sale basis was not recorded; later holdings are not used to fabricate historical performance.

## Where to see it

- A trader detail page shows **Settled cash** and **Pending cash** in **Bank account**. Its holdings table separates **Quantity**, **Settled**, and **Pending** shares.
- The same page has a **Pending settlements** panel with side, company, quantity, cash, trade day, and a label such as **Pending · T+1 · due Day 5**.
- The player panel shows **Total cash**, **Settled cash**, and **Pending cash** under **Balances**. Its **Settlements** tab lists the player's or selected managed fund's pending instructions.
- Completed trades appear immediately in trade history because settlement timing does not delay execution or price formation.

See [Trading days](../rules/trading-days.md) for the market calendar used by T+1 and [Margin accounts](margin.md) for settlement interactions with margin debit.
