# Domain

## Models

### Participant

Fields:

- ID
- Name
- Type (Individual, Company, AIAgent)
- InitialBalance
- CurrentBalance
- Temperament (Aggressive, Balanced, Conservative)
- RiskProfile (High, Medium, Low)

### Company

Fields:

- ID
- Name
- CountOfShares

### Share

Fields:

- ID
- CompanyId
- OwnerId
- InitialPrice
- CurrentPrice
- LastUpdatedAt
- LastTransactionId

### Transaction

Fields:

- ID
- SellerId
- BuyerId
- ShareId
- Amount
- Price
- TotalCost
- TransactionType (Buy, Sell)
- CreatedAt
- UpdatedAt

### Market

Fields:

- Companies (Collection<Company>)
- Shares (Collection<Share>)
- Transactions (Collection<Transaction>)
- Participants (Collection<Participant>)
