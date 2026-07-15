# AI Agent

An AI Agent is a trader whose decisions come from a hosted large-language-model provider instead of the rule-based engine. An operator converts an Individual into an AI Agent from the trader detail page, choosing a provider (GLM or MiniMax) and one of that provider's models. The trader keeps trading as a rule-based Individual until it is converted.

## Provider decisions

- A hosted coordinator runs provider inference beside the market loop. Each eligible AI Agent has at most one request in flight, and requests run outside the market transaction so they never delay a two-second cycle.
- Each agent decides a configurable number of times per trading day rather than on every cycle. The decisions are spread across the day, and the last one is an end-of-day planning call: its orders are not placed that day but are stored and created at the opening cycle of the next trading day, revalidated against the market state at that time. A plan whose target day never opens for the agent is discarded rather than applied late.
- For each turn the coordinator builds a fresh market snapshot, loads the cached project rules, sends one request with no conversation history, and strictly parses the reply. The snapshot includes current exposure, residual executable asks, active and allowed price bounds, the automated-buy envelope, price-time priority ceilings, cancellable open orders, and feedback from the previous application. When a higher residual ask cannot be crossed without jumping earlier demand, the envelope can instead guide a passive bid at the safe priority ceiling.
- The reply must be exactly one JSON object with `summary`, `cancelOrderIds`, and `orders`. Each new order carries a side, company id, positive integer quantity, exact limit price, and reason. Prose-only replies, Markdown fences, missing or unknown fields, non-positive numbers, duplicate cancellation ids, and unknown sides make the whole reply invalid. An empty orders array remains a valid explicit decision to wait.
- The model remains the final authority over side, company, exact limit price, quantity, and reason. The backend never replaces those choices with a supposedly safer price or size: it accepts the exact order or records why it was rejected.
- After reacquiring the market lock, the coordinator processes requested cancellations before new orders. The list must be capped and unique; only the agent's own open or partially filled cancellable orders are eligible, and forced margin or loan orders are never cancelled. The snapshot's buy envelope describes the open-order state before those cancellations; the backend recomputes current exposure, reservations, cash, and margin before validating new orders.
- Successfully parsed orders are applied independently, so one rejected order never blocks another valid one. Buys must fit the shared automated policy as well as current executable supply, price-time priority, LULD, exposure, size, cash, reservation, and margin limits; sells remain subject to ownership and ordinary market checks. Exact accepted prices and quantities persist to the order book and are never re-priced by ordinary order maintenance. They remain exact until fill, agent cancellation, automated expiry, or cancellation by a structural rule such as an invalid price range or stock split. See [Participant rules](../participant-rules.md) and [LULD price controls](../rules/luld.md).
- Cancellation and order outcomes are stored with the call and included in the next stateless snapshot. This lets the model respond to a rejection without relying on hidden conversation history.
- A provider or parsing failure leaves the AI Agent idle in a visible Error state; there is no rule-based fallback. A missing or rejected key surfaces the same Error state and retries on a longer authentication window. Transient errors back off before retrying.

## Configuration and observability

- The provider, model, API key, and maximum decisions per trading day are set per trader on the detail page; the cadence defaults to three. The key is write-only in the interface, stored as provided in the database without encryption, never returned by the API, and never written to a log.
- Conversion is reversible. Converting back to Individual cancels any in-flight request, cancels the trader's open orders, deletes the configuration, and resumes rule-based decisions.
- Every attempted request is recorded before the call and updated with the raw response, parsed decision, application outcome, timing, and token usage. The detail page lists this history newest first with server paging and loads a call's full request and response only when it is opened.
- Call history is retained through provider edits, conversion, pause, restart, and market departure. A full market reset removes it along with the configuration.

## Lifecycle

- An AI Agent shares the non-decision lifecycle of an Individual: dividends, the risk-specific automated margin rule, bankruptcy, fund membership, free-share emissions, starvation-liquidation, and market exit all behave the same. See [Individual](individual.md).
- Market-exit replacements are always rule-based Individuals, so an AI Agent appears only through explicit operator configuration.
