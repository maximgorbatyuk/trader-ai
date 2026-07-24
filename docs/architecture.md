# Architecture

Trader Simulator is a local market simulation with a React frontend, a .NET web API, and SQLite persistence. The application is organized around a cycle-driven market: participants make decisions, orders are maintained and matched, market events change conditions, and the resulting state is exposed to a polling dashboard.

This page describes the durable boundaries and invariants that connect those subsystems. Detailed business rules remain in the focused domain, role, and market-logic pages linked below.

## System boundaries

The frontend is a client of the web API and does not reproduce market behavior. It derives presentation-only aggregates, such as small portfolio groupings, but trading, accounting, lifecycle decisions, and historical records are owned by the backend.

The backend keeps one active market state in SQLite and advances it through serialized cycles. Every seed or reset starts a new market run and retains the run's identity and lifetime metadata, while ordinary market reads are scoped to the active run. A complete advance shares one database transaction across maintenance, decisions, matching, payouts, events, lifecycle work, snapshots, archival, and cycle advancement. A failure in any later phase rolls every earlier mutation back, so API polling exposes either the complete previous cycle or the complete next one.

The simulation is intentionally local and requires no authentication. That keeps the application focused on observing and controlling market behavior rather than user or tenant management.

## Frontend application shell

All pages live beneath one shared application shell. The shell keeps the sidebar, top navigation, market controls, selected trading actor, and shared market state mounted while the active route changes. Pages render only their content area and own any polling that is specific to their data.

Roster pages and detail pages are separate routes. A roster is optimized for finding and sorting entities, while a detail route is the single full view of one participant, company, industry, crisis, or other entity. Deep links resolve to those detail routes rather than rebuilding the same detail surface in multiple modals.

Shared visual and interaction patterns stay centralized. Market and industry maps use the same treemap behavior; sortable and paged tables use common controls; player and fund trades use the same order form and quantity/price presets. The dashboard composes these shared surfaces rather than owning alternative implementations.

The UI follows the product design direction in [Product](../PRODUCT.md): a light trading-terminal interface with dense, legible data, accessible focus states, and text or glyphs accompanying market colors.

## Runtime game settings

Simulation-facing and AI-provider settings are persisted in the `GameSettings` table. `appsettings.json` remains the version-controlled source of defaults: startup inserts catalogued keys that are missing from the table without replacing operator changes already stored there. The shared system prompt every AI trader receives is one such editable setting; because its default text is large, that default is defined in code and supplied as the configuration default rather than inlined in `appsettings.json`. Each provider's connection key is also an editable setting, kept without encryption and typed as a secret: it is accepted through the settings interface but never returned to the client, and a blank submission keeps the stored key rather than clearing it. Request timeout, response-token cap, malformed-response retries, and transport retries have global defaults and safe per-provider overrides. Infrastructure configuration such as connection strings, logging, archive retention, and documentation paths remains outside this mechanism.

The settings catalog owns the editable key allowlist, value type, human-readable name, and description in code. Database rows contain only the stable key and its JSON value, which keeps presentation metadata reviewable with the behavior it documents while avoiding duplicated labels in seeded data.

The settings service validates the complete candidate configuration before saving it, writes accepted changes atomically, and replaces its in-memory snapshot after the write succeeds. Runtime consumers read typed options from that snapshot instead of querying SQLite. A saved value therefore applies when the next market operation or service scan reads its options; work already in progress completes with the snapshot it started with. Settings writes share the market-cycle lock so a configuration change cannot split one cycle across two configurations.

## Market-cycle orchestration

The market advances through an ordered service pipeline. Early phases settle sector mood and detect conditions that must stop or transform trading. Supply-side corporate actions and company lifecycle changes happen before liquidation, participant exits, concentration controls, and auditor decisions. Automated decisions and matching then operate on the resulting market state.

That ordering is part of the model, not an incidental implementation detail. Later phases are allowed to observe changes from earlier phases, while earlier risk checks intentionally operate on the previous settled state. Services therefore share the cycle context and stage related changes within the coordinated advance rather than running as independent background jobs.

Company financial reporting is a distinct pre-lifecycle phase after splits, free-share emissions, primary issuance, and direct investment. It runs only at the trading-day opening and midpoint, persists one immutable report per company and checkpoint, and flushes those reports before lifecycle and audit queries. Audits run in the first cycle of their effective day after concentration control and before automated decisions. They read only completed-day evidence, persist the rating and its scoring evidence, and do not mutate prices or orders. Decision snapshots then join the latest financial checkpoint with the newest audit whose effective day has arrived. See [Company fundamentals](logic/company-fundamentals.md) and [Auditors](roles/auditors.md).

The logical trading clock groups 210 two-second trading cycles into a seven-minute trading day. A separate one-minute break follows each day without advancing the trading-cycle counter; pausing the market freezes either countdown. Trading-day boundaries provide the calendar used by settlement and other day-based rules. See [Trading days](rules/trading-days.md).

Random market behavior is configurable through one shared family of chance and magnitude settings. Deterministic services do not consume random values. Randomized services preserve a stable draw order so seeded simulations and scripted tests remain reproducible when unrelated features are disabled.

## AI traders

An operator can convert an Individual into a provider-backed AI Agent driven by a hosted language model rather than the rule-based engine. Provider inference runs outside the serialized market transaction: a hosted coordinator builds a fresh market snapshot, sends one credential-free-logged request per turn, and strictly deserializes one JSON decision containing a summary, explicit cancellations, an optional Big Investment, new orders, and explicit fixed-horizon price predictions. Prose-only and otherwise malformed replies are errors rather than implicit wait decisions.

The model remains the final authority over each order's side, company, signed price offset, quantity, and reason, and over one optional whole-share direct company investment. The snapshot exposes eligible minimum and maximum share quantities alongside current exposure, residual executable asks, the shared automated-buy envelope, active price bounds, price-time priority ceilings, company financials, effective audit evidence, normalized directional components, and structured feedback from the previous application. The coordinator reacquires the market lock, applies valid owner-requested cancellations first, revalidates and applies the investment second, rebuilds the live market and exposure context, and then resolves each offset against the freshest price and clamps the resulting limit to the allowed band. Quantity is never silently resized. Rejection feedback carries a stable category and relevant corrective bounds while preserving the model's original request.

Each trader keeps at most one request in flight, and configured AI Agents are owned only by the coordinator while Individuals and funds stay with the rule-based engine. Per-trader configuration stores the chosen provider and model, while the provider connection key is a shared per-provider setting rather than a per-trader value; a trader whose provider has no key configured stays idle in a visible Error state until one is added. Relevant project rules are read lazily from an explicit allowlist and cached in memory for five minutes. Every request is audited before the call and updated with its response, decision, predictions, application outcomes, timing, and token usage. Immediate malformed-response retries and same-cycle transport retries share one logical attempt-group identity with increasing attempt numbers and normalized failure categories; a later scheduled decision starts a new group. A provider or parsing failure leaves the trader idle in a visible Error state with no rule-based fallback. See [AI Agent](roles/ai-agent.md).

## Orders, holdings, and prices

A holding is one quantity-based position for a participant and company, with a weighted average cost. It is not one database row per share. A position that is sold out may retain its row with a zero quantity, while active-position reads include only positive quantities.

The issuer's unsold float is implicit: issued shares minus the sum of participant holdings. A company-originated sell can distribute that float without creating a seller holding. Participant sells are constrained by both the owned quantity and quantities already committed to open sell orders.

Buy orders reserve cash when placed. A participant cannot hold open buy and sell interest in the same company at once; order acceptance rejects the opposite side before any balance or holding is reserved, and matching defensively removes a newer legacy self-cross without creating a trade. Matching otherwise supports partial fills and changes economic ownership and cash on the trade date. Every fill also stages a settlement instruction that moves settled cash and share quantities on the next trading day. Primary issuance is exempt from the seller fee because there is no participant seller.

Every new participant sale also records the seller's exact average cost and cost basis at the fill, direct trade and manager fees, gross realized profit or loss, and net realized profit or loss. These values stay attached to the immutable fill rather than being reconstructed later from a changed holding. Financing costs remain in their own ledgers and do not alter trade-level realized performance. See [Trade settlement](logic/settlement.md).

Economic and settled accounting are intentionally separate. Participants can act on an executed position immediately, including reselling a same-day purchase, while settled cash and settled quantity expose the pending T+1 obligation. Due instructions are applied together at a trading-day boundary so same-day chains reconcile without temporary negative settled positions. See [Trade settlement](logic/settlement.md).

Company price is represented by an append-only sequence of price snapshots. The current price is the latest live snapshot for that company, and cycle code uses a shared latest-price lookup rather than repeatedly materializing price history. Retention always keeps each company's newest snapshot in the live set, even when it is older than the normal window, so quiet companies retain a valuation anchor. Capitalization snapshots preserve meaningful company charts across changes in share denomination.

Stock splits and reverse merges change the denomination of holdings, issued supply, cost basis, compatible issuer orders, and the current LULD price band while preserving capitalization as closely as integer quantities allow. Participant orders are cleared so the book can reform around the new denomination. Each action writes an append-only denomination event with the before/after price, supply, and band state; the event is also the lower boundary for rolling trade references, while the original trades remain unchanged. When issuer float is scarce, deterministic primary issuance can answer executable Individual and AI Agent demand that the compatible resting supply cannot satisfy. It adds a priced issuer order at most once per trading day and sends settled proceeds to company cash. Stale replenishment offers outside the active band are cancelled, while initial issuer supply remains standing; later demand first relists already issued float before expanding issued supply. During the existing dividend window, an independent operating-income event can inject cash from the simulated external economy before dividends are funded. Corporate cash has its own append-only ledger, never mixes with participant balances or bank revenue, and makes that external source explicit for reconciliation. See [Corporate cash](logic/corporate-cash.md).

For more detail, see [Share price formation](rules/share-price-formation.md).

## Market state and structural controls

Sector sentiment has two effects. It scales external shocks such as crises, investigations, and scoped news, and separately nudges automated demand toward favored or shunned sectors. Ordinary order matching does not apply a hidden sentiment multiplier to execution prices.

Several structural controls prevent unstable prices or concentrations:

- LULD maintains rolling price bands for each company. Excess in-band buy demand can ratchet the reference upward by a small step; persistent pressure at a band still moves the security through Limit State and Trading Pause without cancelling resting orders, then runs one deterministic reopening auction before continuous trading resumes.
- A concentration control reduces a company that grows beyond the configured share of the market.
- Stock splits and reverse merges keep share denominations within useful bounds.
- Demand-paced primary issuance adds priced supply when issuer float is scarce, while free-share emission separately adds limited free supply to unusually large companies.
- A big investment lets a participant or fund fund a company by buying newly minted shares at the current price, expanding issued supply and crediting corporate cash, and briefly shields the company from delisting. Individuals and funds may still receive the random event; AI Agents are excluded from that roll and can invest only through an explicit provider decision advertised in their current snapshot.
- Dividend ceilings and the issuer's available cash limit corporate payouts.

These mechanisms are distinct from news and crisis impacts. A direct market impact records a new price; a supply change alters quantities and lets later trading discover the price.

See [LULD price controls](rules/luld.md), [Sector sentiment](logic/sector-sentiment.md), [Free-share emission](logic/free-share-emission.md), and [Share price formation](rules/share-price-formation.md).

## Companies, auditors, and crises

The company roster changes over time. New issuers can appear, while persistently weak companies may close. Closing a company cancels its orders and eliminates its holdings without paying owners, and all later order attempts are rejected. The company row is retained so historical prices, ratings, news, and deep links remain resolvable; live-market queries explicitly exclude closed companies.

Large companies receive protection from immediate delisting. When a protected company would otherwise close, it is repriced downward and remains active, allowing a later cycle to reassess it. This separates market-cap protection from permanent immunity.

Auditors are standalone entities rather than trading participants. They review each company over completed two-day windows and publish immutable evidence-backed statuses for the following trading day. Those statuses inform later trader decisions without directly changing prices or standing orders. Keeping auditors outside the participant hierarchy prevents them from accidentally inheriting balances, holdings, loans, bankruptcy, or fund behavior.

A crisis is an active interval rather than a single price shock. It initially affects selected industries and then changes the behavior of risk-sensitive services for its duration. Bankruptcies, company closures, and fund closures are recorded on the crisis timeline when they occur during that window. See [Market crises](logic/crisis.md).

## Participants and collective funds

Individuals, automated agents, collective funds, and the player share the same core order, holding, balance, settlement, margin-account, and loan models. Their differences are expressed through decision and lifecycle rules rather than separate trading engines.

Rule-based Individuals and configured AI Agents share one automated-buy risk policy. It uses risk-specific soft exposure bands, limits order size and passive interest, subtracts capital already reserved by open buys, and permits automated margin only for High-risk traders. Collective Funds and the Player keep their existing fund-specific and manual decision rules. See [Participant rules](participant-rules.md).

A collective fund trades as its own participant. It keeps the founding participant's temperament and risk profile, accepts member deposits up to the configured capacity, returns those deposits when members leave, and can close into a retained inactive record. A fund above the configured capacity returns one newest member through the standard leave path per cycle until it is within the limit. Rule-based Individuals choose among funds using signals such as size, worth, dividends, recent growth, and room remaining; configured AI Agents do not automatically join, open, or switch funds. Sustained growth can also produce news and improve the fund's ability to attract members.

The player may create and directly trade a managed fund. A managed fund remains available to ordinary members, but automated fund-level trading and automatic closure do not take control away from the player. Closing it transfers positions and eligible residual value through an explicit settlement process.

The application maintains one selected trading actor—player or managed fund—across the shell, company views, and order books. This lets shared trade components submit for either actor without maintaining separate trading implementations.

See [Participant rules](participant-rules.md), [Player](roles/player.md), [Collective Fund](roles/collective-fund.md), and [Fund Member](roles/fund-member.md).

## Banking and margin

Margin accounts and explicit term loans are separate liabilities. A matched purchase beyond cash increases the participant's revolving margin debit; it does not create a term loan. Margin interest accrues once per trading day, sales repay interest and debit before releasing free cash, and a maintenance deficiency creates a call whose forced-sale orders use settled holdings and the ordinary order book.

The shared account can support any participant type, but automated discretionary use is narrower: Low- and Medium-risk Individuals and AI Agents buy with cash only, while High-risk automated traders may use margin up to 10% of net worth. This restriction does not change manual Player orders or Collective Fund behavior.

Explicit term loans can be taken by traders — the human player borrows from its own panel — and also cover obligations such as a fund meeting a departing member's payout. Each loan runs a size-scaled term of trading days and is serviced once at the end of each trading day. Loan servicing classifies overdue principal inside remaining principal while tracking overdue interest and assessed fees separately, so principal is never counted twice and participant and bank balances remain reconcilable.

The bank grows through secondary-market trade fees and through loan interest and fees when borrowers pay them. Principal repayment removes an existing liability and is therefore not treated as new bank revenue; unpaid interest or fees do not increase the bank balance.

Both loan and margin liabilities reduce net worth, but they retain independent balances and servicing rules. Persistent term-loan arrears can trigger liquidation under the loan contract; a maintenance deficiency independently creates margin-call sell orders.

See [Margin accounts](logic/margin.md) and [Bank loans](logic/bank-loans.md).

## History and retention

Price, company financial, company audit, participant worth, cash movement, sector sentiment, stock-denomination, realized-sale, free-share-recipient, and AI-prediction histories are recorded forward-only. Features added later do not fabricate older evidence; nullable or unavailable historical fields remain honest, and charts, event trails, and evaluations begin when the corresponding persistence became available.

Company financial checkpoints and audit evidence remain immutable live records for the active market run. A report stores its raw and derived indicators together so later configuration changes cannot rewrite old evidence. An audit links to the exact financial checkpoint and records its completed evaluation window, effective day, component scores, and rule version. Portfolio audit Newswire summaries store their company rows and player/fund ownership quantities as an immutable publication-time snapshot. Company history APIs page these records newest first rather than rebuilding history from current values.

Frequently used history remains in live tables for a configured retention window. Older rows move to archive tables inside the cycle transaction, preserving identifiers while keeping operational queries small. Price retention excludes the newest live snapshot for every company from archival, preserving one current-price anchor even during a long quiet period. Runtime APIs read the live window. Resetting the demo market closes the current run, clears all live and archived simulation evidence and AI configuration, and starts a new active run while preserving operator-managed game settings. Database-generated identifiers are not rewound, so a new run cannot reuse identifiers from the cleared state.

Participant worth is recorded at two grains: once per completed cycle for recent change, and once at each trading-day close for a compact long-horizon series. The per-cycle series ages into its archive, while the smaller daily series is retained so the multi-day total-worth chart stays populated. A migration derives the daily series from the recorded per-cycle worth rather than fabricating it, so it too begins only where real history exists.

AI prediction quality is evaluated only after a forecast horizon matures. Future prices resolve across aligned live and archived price evidence; a company closed before the horizon has a future value of zero, while a window crossing a split or reverse merge is excluded because its price denomination changed. Provider/model comparisons use the intersection of their mature snapshot-cycle ranges, report coverage before and after that common window, and expose directional accuracy, Brier score, calibration, and target-price error. Accuracy and Brier uncertainty use call- or trading-day-clustered intervals; fewer than five clusters is reported as insufficient evidence rather than replaced by an independence assumption.

## Documentation map

- [Domain](domain.md) describes the data model and core market rules.
- [Participant rules](participant-rules.md) links the shared and role-specific participant behavior.
- [Share price formation](rules/share-price-formation.md) explains how matching and direct market events create prices.
- [Company fundamentals](logic/company-fundamentals.md) explains financial checkpoints, derived indicators, and their persisted history.
- [Trading days](rules/trading-days.md) explains the market-wide trading and break schedule.
- [LULD price controls](rules/luld.md) explains security-level bands, pauses, and reopening.
- The [logic](logic/) pages explain focused mechanisms such as settlement, margin, corporate cash, sentiment, share emission, and loans.
- The [roles](roles/) pages explain the behavior of companies, auditors, traders, funds, and the player.
