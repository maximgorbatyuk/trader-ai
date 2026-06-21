# Domain

The app simulates a trading market.

Participants place buy and sell orders during market cycles.
The market matches compatible orders and creates share transactions.
Money transactions record cash reservations and cash movement.
Each share is stored as a separate row and has one current owner.

## Core Rules

- The market runs in cycles.
- Each cycle lets active participants place orders.
- Orders stay open until they are fully filled or cancelled.
- Orders can be partially filled.
- An unfilled order is cancelled automatically once it has rested for too many cycles; cancelling a buy releases its reserved cash and cancelling a sell frees its shares to be listed again.
- While unfilled, a stale order is re-priced toward the market on each later cycle so it has a chance to fill before that cancellation cap.
- A holder that cannot afford any share for several consecutive cycles sells down its most valuable holding to raise cash.
- Every share owner is paid a dividend at a recurring interval, sized as a small percentage of each held share's current price and credited straight to the owner's balance.
- While the market runs, a news event is published automatically every fixed number of cycles; some carry market impact.
- News events can also be created manually, with a chosen target and impact.
- A news event with impact moves the share price of either a single company or every company in one or more industries, up or down, by a percentage of the current price (automated events up to 10%, manually created events up to 95%).
- A market crisis can strike at random, becoming more likely the longer the market runs without one. A local crisis drives a small handful of sectors down; a rarer global crisis drives a large share of all sectors down. Each affected sector falls by its own percentage.
- Any sharp move also clears the resting orders that were priced against the old level: a price drop (from a crisis or a news event) cancels the standing buy orders for the affected companies and releases their reserved cash, while a price rise cancels the standing sell orders and frees their shares to be listed again.
- A buy order reserves cash when it is created.
- The reserved cash amount is `Quantity * LimitPrice`.
- Reserved cash cannot be used by another buy order.
- A buy order can match a sell order when the buy price is greater than or equal to the sell price.
- The execution price is the midpoint of the matched buy and sell limit prices.
- A participant cannot sell shares they do not own.
- A participant cannot create a buy order if they cannot reserve the required cash.
- When a fill uses less than the reserved price, the unused reserved cash for filled shares is released.
- Remaining reserved cash stays with the unfilled part of the buy order.

## Models

### Participant

A participant is a trader in the market.

Fields:

- ID
- Name
- Type (Individual, Company, AIAgent)
- InitialBalance
- CurrentBalance
- ReservedBalance
- Temperament (Aggressive, Balanced, Conservative)
- RiskProfile (High, Medium, Low)
- IsActive

Notes:

- Individual and AI agent participants can trade.
- Company participants can own shares and take part in the market if needed.
- Available cash is `CurrentBalance - ReservedBalance`.
- Temperament and risk profile guide trading decisions.

### Company

A company is the issuer of shares.

Fields:

- ID
- Name
- IndustryId
- IssuedSharesCount
- CreatedAt
- UpdatedAt

Notes:

- Issued shares are represented by separate `Share` records.
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

### Share

A share is one owned unit of a company.

Fields:

- ID
- CompanyId
- OwnerId
- InitialPrice
- CurrentPrice
- LastUpdatedAt
- LastShareTransactionId

Notes:

- A share is owned by at most one participant, or is held unowned by its issuing company until it is first sold.
- A share can be reserved by one open sell order.
- Ownership changes only through a completed share transaction.

### MarketCycle

A market cycle is one simulation step.

Fields:

- ID
- CycleNumber
- Status (Planned, Running, Completed, Failed)
- StartedAt
- CompletedAt

Notes:

- Participants make trading decisions during a cycle.
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
- Status (Open, PartiallyFilled, Filled)
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
- Sell orders offer specific shares for one company.
- A sell order can be company-originated to list the issuer's own shares; such an order has no participant.
- Open orders stay available across cycles.
- The remaining quantity is `Quantity - FilledQuantity`.

### OrderShare

An order share links a sell order to the exact shares being offered.

Fields:

- ID
- OrderId
- ShareId

Notes:

- This model exists because each share is stored as its own row.
- It prevents the same share from being used in more than one open sell order.
- Only sell orders use order shares.

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
- A fill creates money transactions for cash settlement.

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
- Money movement is recorded by money transactions.

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
- A snapshot can be created after a share transaction.
- A snapshot can also be created at the end of a cycle.

### Market

The market is the aggregate state of the simulation.

Fields:

- ID
- Name
- Status (NotStarted, Running, Paused, Completed)
- CurrentCycleId
- CreatedAt
- UpdatedAt

Notes:

- The market contains companies, participants, shares, orders, share transactions, money transactions, and cycles.
- The market controls when cycles start and finish.
- The market owns the order-matching rules.

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
