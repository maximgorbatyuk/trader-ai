# Domain

The app simulates a trading market.

Participants place buy and sell orders during market cycles.
The market matches compatible orders and creates share transactions.
Money transactions record cash reservations and cash movement.
Share ownership is stored as one quantity-based holding per participant and company.

## Core Rules

- The market runs in cycles. A complete cycle is atomic: if any phase fails, maintenance, decisions, matches, payouts, events, lifecycle changes, snapshots, archival, and cycle advancement all remain at the previous completed state.
- Each cycle lets active participants place orders.
- Orders stay open until they are fully filled or cancelled.
- Orders can be partially filled.
- An unfilled order is cancelled automatically once it has rested for too many cycles; cancelling a buy releases its reserved cash and cancelling a sell frees its shares to be listed again.
- While unfilled, a stale order is re-priced toward the market on each later cycle so it has a chance to fill before that cancellation cap.
- A holder that cannot afford any share for several consecutive cycles sells down its most valuable holding to raise cash.
- Every 10–25 trading cycles, each active priced company independently tests for operating income and a dividend. Income comes from the simulated external economy and is credited before any same-window dividend is funded; see [Corporate cash](logic/corporate-cash.md) for the calculation and accounting rules.
- Share owners can be paid a dividend in that window: each company calculates a proportional payout from capitalization and owned shares, then funds no more than its available issuer cash. A shortfall reduces or skips the payout and creates a Newswire item; a stock split leaves what a holder collects unchanged.
- While the market runs, a news event is published automatically every fixed number of cycles; some carry market impact.
- News events can also be created manually, with a chosen target and impact.
- A news event with impact moves the share price of either a single company or every company in one or more industries, up or down, by a percentage of the current price (automated events up to 10%, manually created events up to 95%).
- A market crisis can strike at random, becoming more likely the longer the market runs without one. A local crisis drives a small handful of sectors down; a rarer global crisis drives a large share of all sectors down. Each affected sector falls by its own percentage.
- A science investigation is the upbeat counterpart: a small, local, positive shock that lifts 1–5 sectors by 0.5–5% each, growing likelier after a 50-cycle quiet window. Unlike a crisis or news move it only nudges price up and does not cancel any orders.
- A wealthy trader can go bankrupt, though never during the market's first 500 cycles: after that window, once the market value of its share holdings (cash aside) stays at or above two billion, its chance of collapse rises 0.2% each cycle up to a 10% cap. A bankruptcy wipes the trader's cash and forces it to sell 65% of its holdings, listed 20% below the current price and dropping another 5% each cycle they go unsold until the target is met, with each ask clamped into the executable band so it can cross. It is reported in the newswire but moves no prices and cancels no other trader's orders.
- Cash-strapped traders can pool into a collective fund, though never during the market's first 50 cycles. After that window, a trader holding under the configured join ceiling has a small chance each cycle to join an existing fund or open a new one, and a long stretch unable to afford any share sharply raises those odds. When more than one fund has room, a joiner prefers the stronger one — larger, worth more, and paying more dividends of late. A member hands the fund 90% of its cash and stops bidding for itself (it may still sell shares it already owns), drawing instead a share of the fund's dividends sized by its deposit. Voluntary departure is locked for seven trading days: a Day N joiner first becomes eligible on Day N+7. An AI fund normally keeps 10% of its worth liquid, raises that target to 15% from Day N+6 while a member may soon leave, and returns the full deposit on departure; if cash is still short, it borrows enough to pay in full. A wealthy eligible member may leave once its own balance reaches one hundred million. An eligible non-founder may instead leave to chase a stronger fund, at a chance nudged up for aggressive traders and down for conservative ones, rejoining the best available fund even if its returned cash would otherwise price it out. Whatever the reason, a fund lets at most one member depart voluntarily per trading day; any further leaver waits for the next day. The founder, for its part, winds the fund down once it has clearly failed — its worth collapsed to a fraction of its all-time peak, or no dividend income across its recent payout cycles. When only a pair is left and one leaves, the fund sells everything and splits the proceeds evenly between the two, who then trade on their own again. A collective fund never goes bankrupt.
- A human can join the market as a player: a hand-controlled trader that places and cancels its own orders and collects dividends like any owner, but that the market never manages on its own. Its orders are never re-priced, never cancelled for resting too long, and never cancelled by a crisis or a news event. Stock splits still cancel them; LULD price controls preserve them through a pause and reopening auction. The player never goes bankrupt, never joins a collective fund, and is skipped by the automated decision pass. A market holds at most one player at a time, and a database reset clears it.
- At each completed cycle, a snapshot of the player's cash balance and the value of its holdings is recorded. Comparing the two most recent snapshots gives the player's money and worth changes over the last cycle, while measuring against its starting balance gives the overall change since it joined.
- Any sharp move also clears the resting orders that were priced against the old level: a price drop (from a crisis or a news event) cancels the standing buy orders for the affected companies and releases their reserved cash, while a price rise cancels the standing sell orders and frees their shares to be listed again.
- A buy order reserves cash when it is created.
- The reserved cash amount is `Quantity * LimitPrice`.
- Reserved cash cannot be used by another buy order.
- A buy order can match a sell order when the buy price is greater than or equal to the sell price.
- The execution price is the midpoint of the matched buy and sell limit prices.
- A participant cannot sell shares they do not own.
- Short selling, stock borrow, and buy-to-cover orders are planned for later and are not implemented.
- A participant cannot create a buy order if they cannot reserve the required cash.
- A participant cannot place an order opposite to its own open order for the same company. Legacy self-crosses are cancelled without a fill or price change.
- Closed companies reject every new buy or sell order.
- LULD excludes limits outside the executable band from continuous matching, though participants may still rest orders in a wider allowed range that waits for the band to reach them. Persistent pressure at a band moves the company through Limit State and Trading Pause without cancelling orders, then runs a deterministic reopening auction before returning to Normal.
- When a fill uses less than the reserved price, the unused reserved cash for filled shares is released.
- Remaining reserved cash stays with the unfilled part of the buy order.

A fill changes economic cash, holdings, and price immediately, then a settlement instruction delivers settled cash and shares on the next trading day. Primary issuance credits the company's corporate cash at that settlement boundary. See [Trade settlement](logic/settlement.md) and [Corporate cash](logic/corporate-cash.md).

Margin debit is a revolving participant liability, separate from explicit term loans. It accrues interest once per trading day, is repaid from sale proceeds before free cash, and can create a maintenance call with forced-sale orders. Term loans retain their own repayment and arrears schedule. See [Margin accounts](logic/margin.md) and [Bank loans](logic/bank-loans.md).

## Models

### Participant

A participant is a trader in the market.

Fields:

- ID
- Name
- Type (Individual, Company, AIAgent, CollectiveFund, Player)
- InitialBalance
- CurrentBalance
- SettledCashBalance
- ReservedBalance
- Temperament (Aggressive, Balanced, Conservative)
- RiskProfile (High, Medium, Low)
- IsActive

Notes:

- Individual and AI agent participants can trade.
- Company participants can own shares and take part in the market if needed.
- A player participant is a human-controlled trader; it trades by hand, and the market never manages its orders automatically.
- Current balance is economic cash; settled cash separates completed delivery from pending T+1 cash.
- Available cash is `CurrentBalance - ReservedBalance`.
- Temperament and risk profile guide trading decisions.

### Company

A company is the issuer of shares.

Fields:

- ID
- Name
- IndustryId
- IssuedSharesCount
- CashBalance
- CreatedAt
- UpdatedAt

Notes:

- Issued shares are divided between quantity-based participant holdings and the issuer's implicit unsold float.
- Corporate cash receives settled primary proceeds and simulated operating income, then funds dividends; secondary trades do not affect it. See [Corporate cash](logic/corporate-cash.md).
- The company price can be read from the latest price snapshot.
- Every company belongs to exactly one industry.

### Industry

An industry is a sector that groups companies, used as the unit a news event can move all at once.

Fields:

- ID
- Name

Notes:

- A company belongs to one industry; an industry can hold many companies.
- Industries exist mainly so a single news event can move a whole sector at once.

### Holding

A holding is a participant's position in one company: how many shares it owns and the average price it paid for them.

Fields:

- ID
- ParticipantId
- CompanyId
- Quantity
- SettledQuantity
- AverageCost

Notes:

- A participant has at most one holding per company.
- The issuer's unsold shares are not a holding; that float is the issued supply minus the shares participants hold.
- A buy blends its execution price into the average cost; a sell leaves the average cost of the remaining shares unchanged.
- Economic quantity changes through a completed share transaction; settled quantity changes when its settlement instruction is due.

### MarketCycle

A market cycle is one simulation step.

Fields:

- ID
- CycleNumber
- TradingDayId
- Status (Planned, Running, Completed, Failed)
- StartedAt
- CompletedAt

Notes:

- Participants make trading decisions during a cycle.
- A cycle belongs to one numbered trading day; the separate break does not create a trading cycle.
- The market matches orders during the cycle.
- A cycle can create many share transactions.
- A cycle can create many money transactions.

### Order

An order is an intent to buy or sell shares.

Fields:

- ID
- ParticipantId
- CompanyId
- Type (Buy, Sell)
- Status (Open, PartiallyFilled, Filled, Cancelled)
- Quantity
- FilledQuantity
- LimitPrice
- ReservedCashAmount
- CreatedInCycleId
- CreatedAt
- UpdatedAt

Notes:

- Buy orders request a quantity of shares for one company.
- Buy orders reserve cash at creation.
- Buy order reservation is reduced when cash is spent or released.
- Sell orders offer a quantity from the seller's holding for one company.
- A sell order can be company-originated to list the issuer's own shares; such an order has no participant.
- Open orders stay available across cycles.
- The remaining quantity is `Quantity - FilledQuantity`.

### OrderFill

An order fill records a match between a buy order and a sell order.

Fields:

- ID
- BuyOrderId
- SellOrderId
- Quantity
- ExecutionPrice
- CreatedInCycleId
- ShareTransactionId
- CreatedAt

Notes:

- A fill can complete one order and leave the other order open.
- A large order can have many fills.
- The execution price is the midpoint of the two matched orders' limit prices.
- A fill creates one share transaction.
- A fill creates immediate economic money transactions and one T+1 settlement instruction.

### MoneyTransaction

A money transaction records a cash change.

Fields:

- ID
- ParticipantId
- Type (Reserve, Release, Debit, Credit, Dividend)
- Amount
- RelatedOrderId
- RelatedShareTransactionId
- CreatedInCycleId
- CreatedAt

Notes:

- Reserve records cash blocked for a buy order.
- Release records unused reserved cash returned to available cash.
- Debit records cash spent by a buyer.
- Credit records cash received by a seller.
- Dividend records a periodic payout credited to a share owner.
- Money transactions are the history of balance changes.

### ShareTransaction

A share transaction is a completed share deal.

Fields:

- ID
- SellerId
- BuyerId
- CompanyId
- Quantity
- Price
- TotalCost
- CreatedInCycleId
- CreatedAt
- UpdatedAt

Notes:

- A share transaction moves share ownership from seller to buyer.
- The seller may be the issuing company rather than a participant; in that case no seller cash is credited.
- A share transaction is created from an order fill.
- Economic money movement is recorded by money transactions, while its delivery status is recorded by the linked settlement instruction.

### PriceSnapshot

A price snapshot stores the market price of a company at a point in time.

Fields:

- ID
- CompanyId
- Price
- SourceShareTransactionId
- CreatedInCycleId
- CreatedAt

Notes:

- The latest snapshot is the current company price.
- Historical retention always leaves the newest snapshot for each company in the live set, even when it is older than the normal retention window.
- A snapshot can be created after a share transaction.
- A snapshot can also be created at the end of a cycle.

### Market

The market is the aggregate state of the simulation.

Fields:

- ID
- Name
- Status (NotStarted, Running, Paused, Completed)
- CurrentCycleId
- CurrentTradingDayId
- CreatedAt
- UpdatedAt

Notes:

- The market contains companies, participants, holdings, orders, share transactions, money transactions, and cycles.
- The market controls when cycles start and finish.
- The market owns the order-matching rules.

### TradingDay

A trading day groups 210 active market cycles and the following break under one day number.

Fields:

- ID
- DayNumber
- State (Trading, Break)
- OpenedInCycleId
- ClosedInCycleId

Notes:

- T+1 settlement and daily margin interest use trading-day boundaries rather than raw elapsed time.
- The break has a countdown but no additional market cycle.

### SettlementInstruction

A settlement instruction is the pending or completed delivery obligation created by one share transaction.

Fields:

- ID
- ShareTransactionId
- BuyerId
- SellerId
- CompanyId
- Quantity
- CashAmount
- TradeDayNumber
- DueDayNumber
- Status (Pending, Settled)
- CreatedInCycleId
- SettledInCycleId
- CreatedAt
- SettledAt

Notes:

- Economic cash and quantity change on the trade date; settled balances change on the due trading day.
- Primary issuance has no participant seller and credits company cash at settlement.
- Margin advances and repayments carried by the instruction keep economic and settled cash reconcilable.

### MarginAccount

A margin account holds one participant's revolving securities-financing liability.

Fields:

- ID
- ParticipantId
- DebitBalance
- AccruedInterest
- InitialMarginRate
- MaintenanceMarginRate
- Status (Active, UnderCall, Closed)
- LastInterestAccruedTradingDayId

Notes:

- Margin debit and accrued interest are separate from explicit term loans.
- Buying power uses account equity and the initial requirement; maintenance standing determines whether a call is needed.

### MarginCall

A margin call records a margin account's maintenance deficiency.

Fields:

- ID
- MarginAccountId
- OpenedInTradingDayId
- OpenedInCycleId
- ClosedInTradingDayId
- AccountEquity
- MaintenanceRequirement
- Deficiency
- Status (Open, Satisfied)
- CreatedAt
- ClosedAt

Notes:

- An open call can create discounted sell orders from settled holdings.
- The call is satisfied when account equity again meets maintenance.

### CorporateCashTransaction

A corporate cash transaction is append-only evidence for an issuer cash credit or debit.

Fields:

- ID
- CompanyId
- Type (PrimaryIssuance, OperatingIncome, DividendDeclared, ClosureDistribution)
- Amount
- CreatedInCycleId
- CreatedAt

Notes:

- Primary issuance credits company cash after T+1 settlement.
- Operating income credits company cash from the simulated external economy during a dividend window.
- A funded dividend debits company cash by exactly the amount allocated to participants.

### PriceBandState

A price-band state stores one company's current LULD control state.

Fields:

- CompanyId
- State (Normal, LimitState, TradingPause, Reopening)
- LimitDirection (Upper, Lower)
- ReferencePrice
- LowerBandPrice
- UpperBandPrice
- LimitStateStartedCycleNumber
- PauseUntilCycleNumber
- UpdatedInCycleId

Notes:

- The rolling reference and band determine which limits are eligible for continuous matching. The wider allowed order range that bounds where any participant order may rest is derived from the same reference and is not stored as extra columns.
- State transitions preserve resting orders and lead to a deterministic reopening auction after a trading pause.

### NewsPost

A news post is a social-media style event, generated automatically every fixed number of cycles while the market runs, or created manually.

Fields:

- ID
- Title
- Content
- PublishedInCycleId
- PublishedAt
- Scope (None, Company, Industries)
- Direction (Increase, Decrease)
- ImpactPercent
- TargetCompanyId
- Industries (the impacted industries when the scope is Industries)

Notes:

- Title and content are generated to read like real, if whimsical, news.
- A post with no impact has scope None and no direction, percent, or target.
- A company-scoped post moves one company; an industry-scoped post moves every company in its listed industries.
- Impact is applied by recording a new price point for each affected company, the same way a trade moves the price.

### Crisis

A crisis is a market shock that drives several industries down at once.

Fields:

- ID
- Title
- Content
- Scope (Local, Global)
- TriggeredInCycleId
- TriggeredAt
- Industries (each affected industry with its own decrease)

Notes:

- A crisis becomes more likely the longer the market goes without one of its scope, and its clock resets when it fires.
- A local crisis hits a few sectors; a global crisis hits a large share of all sectors.
- Each affected industry falls by its own percentage, applied by recording a new price point for every company in that industry.
- The drop also cancels the standing buy orders for the affected companies.

### ScienceInvestigation

A science investigation is a small, positive market event — a research breakthrough — that lifts a few industries at once.

Fields:

- ID
- Title
- Content
- TriggeredInCycleId
- TriggeredAt
- Industries (each lifted industry with its own increase)

Notes:

- A science investigation is always local: it lifts 1 to 5 sectors, each by its own increase between 0.5% and 5%.
- It becomes more likely the longer the market goes without one, after a short quiet window, and its clock resets when it fires.
- Each lifted industry rises by its own percentage, applied by recording a new price point for every company in that industry.
- It only nudges price up and never cancels any orders.

### Bankruptcy

A bankruptcy is the collapse of a single wealthy trader, recorded so it can be shown as a newswire event.

Fields:

- ID
- ParticipantId
- Title
- Content
- CashLost
- ShareWorth
- TriggeredInCycleId
- TriggeredAt

Notes:

- A trader is at risk only while the market value of its share holdings stays at or above a high wealth line, regardless of its cash; the longer it stays above, the more likely bankruptcy becomes, up to a ceiling. No trader is ever at risk during the market's opening cycles.
- When it fires the trader loses all of its cash and most of its holdings are listed for sale below the current price.
- Unsold forced-sale orders are re-listed a step cheaper each cycle until the sell-down target is reached.
- A bankruptcy carries no price impact and cancels no other trader's orders; only the bankrupt trader's own orders are affected.

### CollectiveFund

A collective fund is a pooled investment vehicle that trades as its own participant, holding the cash its members contribute.

Fields:

- ID
- ParticipantId (the participant the fund trades through)
- FoundedByParticipantId
- Status (Active, GoingToBeClosed, Closed)
- CreatedInCycleId
- CreatedAt
- ClosedAt

Notes:

- The fund trades the pooled cash like any participant while keeping part of its worth liquid so it can return deposits on demand.
- The fund's own dividends are partly passed through to its members in proportion to their deposits, less a management fee the fund keeps.
- A fund that is winding down only sells; once it holds nothing its cash is split evenly among the remaining members and it closes.

### CollectiveFundParticipant

A collective fund participant records one trader's membership in a fund.

Fields:

- ID
- CollectiveFundId
- ParticipantId
- JoinedAt
- JoinedInCycleId
- DepositAmount
- LeaveRampCycles
- IsLeaving

Notes:

- A participant belongs to at most one fund at a time.
- While the membership exists the member places no buy orders of its own, though it may keep selling shares it already holds.
- The deposit is returned in full when the member leaves, and the membership ends when the trader leaves or the fund closes.

### CollectiveFundMembershipEvent

A collective fund membership event is an append-only record of a member joining or leaving a fund, kept so a fund's or a trader's membership history stays available after the membership row itself is gone.

Fields:

- ID
- CollectiveFundId
- FundParticipantId (the fund's own participant)
- ParticipantId (the member)
- Type (Joined, Left)
- Amount (the deposit contributed on a join, or the payout returned on a leave)
- CreatedInCycleId
- CreatedAt

Notes:

- A join records the contributed deposit; a leave records the returned payout, which is zero when a closing fund could return nothing or the member had already left the market.
- The same history is read from either side: a trader's page lists the funds it joined or left, and a fund's page lists the members who joined or left it.
- The records survive a member leaving the market but are cleared on a database reset.
