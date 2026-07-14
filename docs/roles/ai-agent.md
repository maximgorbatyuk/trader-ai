# AI Agent

An AI Agent is a trader whose decisions come from a hosted large-language-model provider instead of the rule-based engine. An operator converts an Individual into an AI Agent from the trader detail page, choosing a provider (GLM or MiniMax) and one of that provider's models. The trader keeps trading as a rule-based Individual until it is converted.

## Provider decisions

- A hosted coordinator runs provider inference beside the market loop. Each eligible AI Agent has at most one request in flight, and requests run outside the market transaction so they never delay a two-second cycle.
- For each turn the coordinator builds a fresh market snapshot, loads the cached project rules, sends one request with no conversation history, and strictly parses the reply. It then reacquires the market lock only to revalidate and place the still-valid orders.
- The reply must be exactly one JSON object: a short summary and an orders array. An empty orders array is a valid decision to wait. Each order carries a side, company id, quantity, limit price, and a reason. A reply that carries no JSON object at all — only prose — is treated as the same decision to wait, keeping the model's text as the summary rather than surfacing an error.
- Deserialization is otherwise strict — a Markdown fence, unknown fields, non-positive numbers, or an unknown side make the whole reply invalid — but successfully parsed orders are applied independently, so one invalid order never blocks another valid one.
- Applied orders face the same validation as any order: no short selling, only owned shares may be sold, and market break, delisting, halt, allowed price range, buying power, and cash reservation are all enforced at application time. Orders rest in the book and match only when the normal cycle advances. See [LULD price controls](../rules/luld.md).
- A provider or parsing failure leaves the AI Agent idle in a visible Error state; there is no rule-based fallback. A missing or rejected key surfaces the same Error state and retries on a longer authentication window. Transient errors back off before retrying.

## Configuration and observability

- The provider, model, and API key are set per trader on the detail page. The key is write-only in the interface, stored as provided in the database without encryption, never returned by the API, and never written to a log.
- Conversion is reversible. Converting back to Individual cancels any in-flight request, cancels the trader's open orders, deletes the configuration, and resumes rule-based decisions.
- Every attempted request is recorded before the call and updated with the raw response, parsed decision, application outcome, timing, and token usage. The detail page lists this history newest first with server paging and loads a call's full request and response only when it is opened.
- Call history is retained through provider edits, conversion, pause, restart, and market departure. A full market reset removes it along with the configuration.

## Lifecycle

- An AI Agent shares the non-decision lifecycle of an Individual: dividends, margin buying power, bankruptcy, fund membership, free-share emissions, starvation-liquidation, and market exit all behave the same. See [Individual](individual.md).
- Market-exit replacements are always rule-based Individuals, so an AI Agent appears only through explicit operator configuration.
