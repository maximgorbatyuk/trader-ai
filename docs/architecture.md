# Architecture

Trader Simulator is a local market simulation with a React frontend, a .NET web API, and SQLite persistence. The application is organized around a cycle-driven market: participants make decisions, orders are maintained and matched, market events change conditions, and the resulting state is exposed to a polling dashboard.

This page describes the durable boundaries and invariants that connect those subsystems. Detailed business rules remain in the focused domain, role, and market-logic pages linked below.

## System boundaries

The frontend is a client of the web API and does not reproduce market behavior. It derives presentation-only aggregates, such as small portfolio groupings, but trading, accounting, lifecycle decisions, and historical records are owned by the backend.

The backend keeps one market state in SQLite and advances it through serialized cycles. An advance is treated as one coordinated operation so that orders, balances, holdings, prices, events, and snapshots cannot expose a partially completed cycle.

The simulation is intentionally local and requires no authentication. That keeps the application focused on observing and controlling market behavior rather than user or tenant management.

## Frontend application shell

All pages live beneath one shared application shell. The shell keeps the sidebar, top navigation, footer, market controls, selected trading actor, and shared market state mounted while the active route changes. Pages render only their content area and own any polling that is specific to their data.

Roster pages and detail pages are separate routes. A roster is optimized for finding and sorting entities, while a detail route is the single full view of one participant, company, industry, crisis, or other entity. Deep links resolve to those detail routes rather than rebuilding the same detail surface in multiple modals.

Shared visual and interaction patterns stay centralized. Market and industry maps use the same treemap behavior; sortable and paged tables use common controls; player and fund trades use the same order form and quantity/price presets. The dashboard composes these shared surfaces rather than owning alternative implementations.

The UI follows the product design direction in [Product](../PRODUCT.md): a light trading-terminal interface with dense, legible data, accessible focus states, and text or glyphs accompanying market colors.

## Market-cycle orchestration

The market advances through an ordered service pipeline. Early phases settle sector mood and detect conditions that must stop or transform trading. Supply-side corporate actions and company lifecycle changes happen before liquidation, participant exits, concentration controls, and auditor decisions. Automated decisions and matching then operate on the resulting market state.

That ordering is part of the model, not an incidental implementation detail. Later phases are allowed to observe changes from earlier phases, while earlier risk checks intentionally operate on the previous settled state. Services therefore share the cycle context and stage related changes within the coordinated advance rather than running as independent background jobs.

Random market behavior is configurable through one shared family of chance and magnitude settings. Deterministic services do not consume random values. Randomized services preserve a stable draw order so seeded simulations and scripted tests remain reproducible when unrelated features are disabled.

## Orders, holdings, and prices

A holding is one quantity-based position for a participant and company, with a weighted average cost. It is not one database row per share. A position that is sold out may retain its row with a zero quantity, while active-position reads include only positive quantities.

The issuer's unsold float is implicit: issued shares minus the sum of participant holdings. A company-originated sell can distribute that float without creating a seller holding. Participant sells are constrained by both the owned quantity and quantities already committed to open sell orders.

Buy orders reserve cash when placed. Matching supports partial fills and settles ownership, reservations, buyer debits, seller credits, and any secondary-market fee as separate accounting movements. Primary issuance is exempt from the seller fee because there is no participant seller.

Company price is represented by an append-only sequence of price snapshots. The current price is the latest snapshot for that company, and cycle code uses a shared latest-price lookup rather than repeatedly materializing price history. Capitalization snapshots preserve meaningful company charts across changes in share denomination.

Stock splits and reverse merges change the denomination of holdings, issued supply, cost basis, and compatible issuer orders while preserving capitalization as closely as integer quantities allow. Participant orders are cleared so the book can reform around the new denomination. Dividends are based on company capitalization and owned shares, with stability checks and a cash ceiling preventing unbounded money creation.

For more detail, see [Share price formation](rules/share-price-formation.md).

## Market state and structural controls

Sector sentiment has two effects. It scales external shocks such as crises, investigations, and scoped news, and separately nudges automated demand toward favored or shunned sectors. Ordinary order matching does not apply a hidden sentiment multiplier to execution prices.

Several structural controls prevent unstable prices or concentrations:

- A volatility halt freezes a company after an excessive prior-cycle move and clears orders that were priced before the halt.
- A concentration control reduces a company that grows beyond the configured share of the market.
- Stock splits and reverse merges keep share denominations within useful bounds.
- Free-share emission adds limited supply to unusually large companies without directly setting a new price.
- Dividend ceilings limit cash creation as capitalization grows.

These mechanisms are distinct from news and crisis impacts. A direct market impact records a new price; a supply change alters quantities and lets later trading discover the price.

See [Sector sentiment](logic/sector-sentiment.md), [Free-share emission](logic/free-share-emission.md), and [Share price formation](rules/share-price-formation.md).

## Companies, auditors, and crises

The company roster changes over time. New issuers can appear, while persistently weak companies may close. Closing a company cancels its orders and eliminates its holdings without paying owners. The company row is retained so historical prices, ratings, news, and deep links remain resolvable; live-market queries explicitly exclude closed companies.

Large companies receive protection from immediate delisting. When a protected company would otherwise close, it is repriced downward and remains active, allowing a later cycle to reassess it. This separates market-cap protection from permanent immunity.

Auditors are standalone entities rather than trading participants. They review companies, publish ratings, and may trigger a price reduction or revision of stale buy interest. Keeping auditors outside the participant hierarchy prevents them from accidentally inheriting balances, holdings, loans, bankruptcy, or fund behavior.

A crisis is an active interval rather than a single price shock. It initially affects selected industries and then changes the behavior of risk-sensitive services for its duration. Events such as ratings, bankruptcies, company closures, and fund closures are recorded on the crisis timeline when they occur during that window.

## Participants and collective funds

Individuals, automated agents, collective funds, and the player share the same core order, holding, balance, and loan model. Their differences are expressed through decision and lifecycle rules rather than separate trading engines.

A collective fund trades as its own participant. It keeps the founding participant's temperament and risk profile, accepts member deposits, returns those deposits when members leave, and can close into a retained inactive record. Members choose among funds using signals such as size, worth, dividends, and recent growth. Sustained growth can also produce news and improve the fund's ability to attract members.

The player may create and directly trade a managed fund. A managed fund remains available to ordinary members, but automated fund-level trading and automatic closure do not take control away from the player. Closing it transfers positions and eligible residual value through an explicit settlement process.

The application maintains one selected trading actor—player or managed fund—across the shell, company views, and order books. This lets shared trade components submit for either actor without maintaining separate trading implementations.

See [Participant rules](participant-rules.md), [Player](roles/player.md), [Collective Fund](roles/collective-fund.md), and [Fund Member](roles/fund-member.md).

## Banking and margin

Margin debt is represented by explicit loans rather than leaving a participant balance negative. When a participant needs borrowed cash — a matched purchase beyond its cash, or a fund covering a departing member's deposit — the backend creates a loan, credits the proceeds, and records the disbursement. Loan servicing separates principal, interest, fines, and repayment movements so participant and bank balances remain reconcilable.

The bank grows through secondary-market trade fees and loan interest. Principal repayment removes an existing liability and is therefore not treated as new bank revenue.

Loan liability reduces net worth and future borrowing capacity. Persistent arrears can trigger forced liquidation, including for the player, because that consequence belongs to the accepted loan terms rather than automated trading behavior.

See [Bank loans](logic/bank-loans.md).

## History and retention

Price, participant worth, cash movement, and sector sentiment histories are recorded as forward-only snapshots. Features added later do not fabricate older history; charts begin when the corresponding snapshots became available.

Frequently used history remains in live tables for a configured retention window. Older rows move to archive tables inside the cycle transaction, preserving identifiers while keeping operational queries small. Runtime APIs read the live window, and resetting the demo market clears both live and archived state.

## Documentation map

- [Domain](domain.md) describes the data model and core market rules.
- [Participant rules](participant-rules.md) links the shared and role-specific participant behavior.
- [Share price formation](rules/share-price-formation.md) explains how matching and direct market events create prices.
- The [logic](logic/) pages explain focused mechanisms such as sentiment, share emission, and loans.
- The [roles](roles/) pages explain the behavior of companies, auditors, traders, funds, and the player.
