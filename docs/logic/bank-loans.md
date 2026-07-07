# Bank loans

Buying on margin opens a bank loan. Debt is a first-class liability that a trader repays over time, rather than a running negative balance, so a participant's cash balance is never negative.

## Rules

- A single bank, the "National bank", lends to every trader. It has no balance of its own; it only sets the interest rate.
- A trader may borrow up to a fixed fraction of its total worth (cash plus holdings). Every active trader type can borrow, including the human player and collective funds.
- When a buy fills for more cash than the buyer holds, a loan is opened for the shortfall plus a small cash buffer, and the buffer is left in the buyer's balance after the purchase settles.
- Each loan snapshots the bank's interest rate when it opens, so a later rate change never touches existing loans.
- A loan's term is set once at origination and scales with its size against the borrower's worth: a larger loan relative to worth runs a longer term, within a fixed minimum and maximum.
- Each cycle the loan is charged a scheduled repayment plus interest, paid from the borrower's cash and recorded on the trader's cash movements.
- With more than one loan, cash pays the oldest loan first.
- A missed or partial payment carries the unpaid amount to the next cycle with a fine added on top, tracked as the loan's past-due amount.
- In the final stretch of a loan's term, a borrower still behind on payments is forced to sell shares below the market price to raise cash fast — the same forced-sale mechanism as a bankruptcy, and it applies to the human player too as a deliberate exception, because the player agreed to the loan's terms. This does not wipe the borrower's cash or mark it bankrupt.
- Carrying more debt raises a trader's bankruptcy risk and makes automated traders more willing to sell to deleverage.
- A loan closes when it is fully repaid, or when its borrower leaves the market through bankruptcy, departure, or a fund wind-down — in which case the debt is discharged.
- Net worth shown across the app subtracts open-loan debt, while gross holdings value stays visible separately.
- Existing debt from before this feature is migrated into loans on first launch, leaving no negative balances.

## Where to see loans

- The Banks page lists each bank with its rate and current lending book.
- The Bank loans page is a filterable roster of every loan by bank and status.
- The player panel lists the player's own loans with buttons to repay part or all of a loan.
- Each trader's page shows its loans, and loan activity appears on the trader's cash movements.
