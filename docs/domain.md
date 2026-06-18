# Domain

The app simulates a trading market.

Participants place buy and sell orders during market cycles.
The market matches compatible orders and creates share transactions.
Money transactions record cash reservations and cash movement.
Each share is stored as a separate row and has one current owner.

## Core Rules

- The market runs in cycles.
- Each cycle lets active participants place orders.
- Orders stay open until they are fully filled.
- Orders can be partially filled.
- Order cancellation is not part of the first implementation.
- A buy order reserves cash when it is created.
- The reserved cash amount is `Quantity * LimitPrice`.
- Reserved cash cannot be used by another buy order.
- A buy order can match a sell order when the buy price is greater than or equal to the sell price.
- The execution price is the price from the older order.
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
- IssuedSharesCount
- CreatedAt
- UpdatedAt

Notes:

- Issued shares are represented by separate `Share` records.
- The company price can be read from the latest price snapshot.

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

- Each share has exactly one owner.
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
- The execution price comes from the older matched order.
- A fill creates one share transaction.
- A fill creates money transactions for cash settlement.

### MoneyTransaction

A money transaction records a cash change.

Fields:

- ID
- ParticipantId
- Type (Reserve, Release, Debit, Credit)
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
