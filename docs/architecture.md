# Architecture

Trader Simulator is a local market simulation with a React frontend, a .NET web API, and SQLite persistence. The application is organized around a cycle-driven market: participants make decisions, orders are maintained and matched, market events change conditions, and the resulting state is exposed to a polling dashboard.

This page describes the durable boundaries and invariants that connect those subsystems. Detailed business rules remain in the focused domain, role, and market-logic pages linked below.

## System boundaries

The frontend is a client of the web API and does not reproduce market behavior. It derives presentation-only aggregates, such as small portfolio groupings, but trading, accounting, lifecycle decisions, and historical records are owned by the backend.

The backend keeps one market state in SQLite and advances it through serialized cycles. A complete advance shares one database transaction across maintenance, decisions, matching, payouts, events, lifecycle work, snapshots, archival, and cycle advancement. A failure in any later phase rolls every earlier mutation back, so API polling exposes either the complete previous cycle or the complete next one.

The simulation is intentionally local and requires no authentication. That keeps the application focused on observing and controlling market behavior rather than user or tenant management.

## Frontend application shell

All pages live beneath one shared application shell. The shell keeps the sidebar, top navigation, footer, market controls, selected trading actor, and shared market state mounted while the active route changes. Pages render only their content area and own any polling that is specific to their data.

Roster pages and detail pages are separate routes. A roster is optimized for finding and sorting entities, while a detail route is the single full view of one participant, company, industry, crisis, or other entity. Deep links resolve to those detail routes rather than rebuilding the same detail surface in multiple modals.

Shared visual and interaction patterns stay centralized. Market and industry maps use the same treemap behavior; sortable and paged tables use common controls; player and fund trades use the same order form and quantity/price presets. The dashboard composes these shared surfaces rather than owning alternative implementations.

The UI follows the product design direction in [Product](../PRODUCT.md): a light trading-terminal interface with dense, legible data, accessible focus states, and text or glyphs accompanying market colors.

## Market-cycle orchestration

The market advances through an ordered service pipeline. Early phases settle sector mood and detect conditions that must stop or transform trading. Supply-side corporate actions and company lifecycle changes happen before liquidation, participant exits, concentration controls, and auditor decisions. Automated decisions and matching then operate on the resulting market state.

That ordering is part of the model, not an incidental implementation detail. Later phases are allowed to observe changes from earlier phases, while earlier risk checks intentionally operate on the previous settled state. Services therefore share the cycle context and stage related changes within the coordinated advance rather than running as independent background jobs.

The logical trading clock groups 210 two-second trading cycles into a seven-minute trading day. A separate one-minute break follows each day without advancing the trading-cycle counter; pausing the market freezes either countdown. Trading-day boundaries provide the calendar used by settlement and other day-based rules. See [Trading days](rules/trading-days.md).

Random market behavior is configurable through one shared family of chance and magnitude settings. Deterministic services do not consume random values. Randomized services preserve a stable draw order so seeded simulations and scripted tests remain reproducible when unrelated features are disabled.

## AI traders

An operator can convert an Individual into a provider-backed AI Agent driven by a hosted language model rather than the rule-based engine. Provider inference runs outside the serialized market transaction: a hosted coordinator builds a fresh market snapshot, sends one credential-free-logged request per turn, and strictly deserializes a JSON order decision. It reacquires the shared market lock only to revalidate and place still-valid orders through the ordinary order path, so an AI order faces the same LULD, price-range, buying-power, reservation, and owned-share checks as any other and never delays a cycle.

Each trader keeps at most one request in flight, and configured AI Agents are owned only by the coordinator while Individuals and funds stay with the rule-based engine. Per-trader configuration stores the provider, chosen model, and an API key that is write-only in the interface, kept without encryption, never returned by the API, and never logged. Relevant project rules are read lazily from an explicit allowlist and cached in memory for five minutes. Every request is audited to the database before the call and updated with its response, decision, application outcome, timing, and token usage; a provider or parsing failure leaves the trader idle in a visible Error state with no rule-based fallback. See [AI Agent](roles/ai-agent.md).

## Orders, holdings, and prices

A holding is one quantity-based position for a participant and company, with a weighted average cost. It is not one database row per share. A position that is sold out may retain its row with a zero quantity, while active-position reads include only positive quantities.

The issuer's unsold float is implicit: issued shares minus the sum of participant holdings. A company-originated sell can distribute that float without creating a seller holding. Participant sells are constrained by both the owned quantity and quantities already committed to open sell orders.

Buy orders reserve cash when placed. A participant cannot hold open buy and sell interest in the same company at once; order acceptance rejects the opposite side before any balance or holding is reserved, and matching defensively removes a newer legacy self-cross without creating a trade. Matching otherwise supports partial fills and changes economic ownership and cash on the trade date. Every fill also stages a settlement instruction that moves settled cash and share quantities on the next trading day. Primary issuance is exempt from the seller fee because there is no participant seller.

Economic and settled accounting are intentionally separate. Participants can act on an executed position immediately, including reselling a same-day purchase, while settled cash and settled quantity expose the pending T+1 obligation. Due instructions are applied together at a trading-day boundary so same-day chains reconcile without temporary negative settled positions. See [Trade settlement](logic/settlement.md).

Company price is represented by an append-only sequence of price snapshots. The current price is the latest live snapshot for that company, and cycle code uses a shared latest-price lookup rather than repeatedly materializing price history. Retention always keeps each company's newest snapshot in the live set, even when it is older than the normal window, so quiet companies retain a valuation anchor. Capitalization snapshots preserve meaningful company charts across changes in share denomination.

Stock splits and reverse merges change the denomination of holdings, issued supply, cost basis, and compatible issuer orders while preserving capitalization as closely as integer quantities allow. Participant orders are cleared so the book can reform around the new denomination. Scarce issuer float can trigger demand-paced primary issuance, which adds a priced issuer order at most once per trading day and sends settled proceeds to company cash. During the existing dividend window, an independent operating-income event can inject cash from the simulated external economy before dividends are funded. Corporate cash has its own append-only ledger, never mixes with participant balances or bank revenue, and makes that external source explicit for reconciliation. See [Corporate cash](logic/corporate-cash.md).

For more detail, see [Share price formation](rules/share-price-formation.md).

## Market state and structural controls

Sector sentiment has two effects. It scales external shocks such as crises, investigations, and scoped news, and separately nudges automated demand toward favored or shunned sectors. Ordinary order matching does not apply a hidden sentiment multiplier to execution prices.

Several structural controls prevent unstable prices or concentrations:

- LULD maintains rolling price bands for each company. Excess in-band buy demand can ratchet the reference upward by a small step; persistent pressure at a band still moves the security through Limit State and Trading Pause without cancelling resting orders, then runs one deterministic reopening auction before continuous trading resumes.
- A concentration control reduces a company that grows beyond the configured share of the market.
- Stock splits and reverse merges keep share denominations within useful bounds.
- Demand-paced primary issuance adds priced supply when issuer float is scarce, while free-share emission separately adds limited free supply to unusually large companies.
- Dividend ceilings and the issuer's available cash limit corporate payouts.

These mechanisms are distinct from news and crisis impacts. A direct market impact records a new price; a supply change alters quantities and lets later trading discover the price.

See [LULD price controls](rules/luld.md), [Sector sentiment](logic/sector-sentiment.md), [Free-share emission](logic/free-share-emission.md), and [Share price formation](rules/share-price-formation.md).

## Companies, auditors, and crises

The company roster changes over time. New issuers can appear, while persistently weak companies may close. Closing a company cancels its orders and eliminates its holdings without paying owners, and all later order attempts are rejected. The company row is retained so historical prices, ratings, news, and deep links remain resolvable; live-market queries explicitly exclude closed companies.

Large companies receive protection from immediate delisting. When a protected company would otherwise close, it is repriced downward and remains active, allowing a later cycle to reassess it. This separates market-cap protection from permanent immunity.

Auditors are standalone entities rather than trading participants. They review companies, publish ratings, and may trigger a price reduction with stale-buy revisions or a positive expectations lift with eligible stale-sell cancellation. Keeping auditors outside the participant hierarchy prevents them from accidentally inheriting balances, holdings, loans, bankruptcy, or fund behavior.

A crisis is an active interval rather than a single price shock. It initially affects selected industries and then changes the behavior of risk-sensitive services for its duration. Events such as ratings, bankruptcies, company closures, and fund closures are recorded on the crisis timeline when they occur during that window. See [Market crises](logic/crisis.md).

## Participants and collective funds

Individuals, automated agents, collective funds, and the player share the same core order, holding, balance, settlement, margin-account, and loan models. Their differences are expressed through decision and lifecycle rules rather than separate trading engines.

A collective fund trades as its own participant. It keeps the founding participant's temperament and risk profile, accepts member deposits up to the configured capacity, returns those deposits when members leave, and can close into a retained inactive record. A fund above the configured capacity returns one newest member through the standard leave path per cycle until it is within the limit. Members choose among funds using signals such as size, worth, dividends, recent growth, and room remaining. Sustained growth can also produce news and improve the fund's ability to attract members.

The player may create and directly trade a managed fund. A managed fund remains available to ordinary members, but automated fund-level trading and automatic closure do not take control away from the player. Closing it transfers positions and eligible residual value through an explicit settlement process.

The application maintains one selected trading actor—player or managed fund—across the shell, company views, and order books. This lets shared trade components submit for either actor without maintaining separate trading implementations.

See [Participant rules](participant-rules.md), [Player](roles/player.md), [Collective Fund](roles/collective-fund.md), and [Fund Member](roles/fund-member.md).

## Banking and margin

Margin accounts and explicit term loans are separate liabilities. A matched purchase beyond cash increases the participant's revolving margin debit; it does not create a term loan. Margin interest accrues once per trading day, sales repay interest and debit before releasing free cash, and a maintenance deficiency creates a call whose forced-sale orders use settled holdings and the ordinary order book.

Explicit term loans remain available for workflows such as a fund meeting a departing member's payout. Loan servicing classifies overdue principal inside remaining principal while tracking overdue interest and assessed fees separately, so principal is never counted twice and participant and bank balances remain reconcilable.

The bank grows through secondary-market trade fees and through loan interest and fees when borrowers pay them. Principal repayment removes an existing liability and is therefore not treated as new bank revenue; unpaid interest or fees do not increase the bank balance.

Both loan and margin liabilities reduce net worth, but they retain independent balances and servicing rules. Persistent term-loan arrears can trigger liquidation under the loan contract; a maintenance deficiency independently creates margin-call sell orders.

See [Margin accounts](logic/margin.md) and [Bank loans](logic/bank-loans.md).

## History and retention

Price, participant worth, cash movement, and sector sentiment histories are recorded as forward-only snapshots. Features added later do not fabricate older history; charts begin when the corresponding snapshots became available.

Frequently used history remains in live tables for a configured retention window. Older rows move to archive tables inside the cycle transaction, preserving identifiers while keeping operational queries small. Price retention excludes the newest live snapshot for every company from archival, preserving one current-price anchor even during a long quiet period. Runtime APIs read the live window, and resetting the demo market clears both live and archived state.

## Documentation map

- [Domain](domain.md) describes the data model and core market rules.
- [Participant rules](participant-rules.md) links the shared and role-specific participant behavior.
- [Share price formation](rules/share-price-formation.md) explains how matching and direct market events create prices.
- [Trading days](rules/trading-days.md) explains the market-wide trading and break schedule.
- [LULD price controls](rules/luld.md) explains security-level bands, pauses, and reopening.
- The [logic](logic/) pages explain focused mechanisms such as settlement, margin, corporate cash, sentiment, share emission, and loans.
- The [roles](roles/) pages explain the behavior of companies, auditors, traders, funds, and the player.
