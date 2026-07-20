# Bank loans

Bank loans are explicit fixed-term liabilities that a trader repays over time. They are separate from revolving margin accounts: buying beyond available cash increases margin debit and does not originate a bank loan.

## Rules

- A single bank, the "National bank", lends to every trader and keeps an accumulated revenue balance. Secondary-market transaction fees and loan interest or fees increase that balance only when paid; loan principal and unpaid charges do not.
- A trader may borrow up to a fixed fraction of its total worth (cash plus holdings). The human player takes loans from its own panel, and a collective fund borrows to meet obligations; every active trader type can carry a term loan.
- A securities purchase never opens a term loan. Its cash shortfall is handled by the participant's margin account under [Margin accounts](margin.md).
- A collective fund also opens a loan when it lacks the cash to return a departing member's deposit: it borrows the shortfall plus a small buffer, pays the member in full that cycle, and repays the loan through the normal servicing below. Because returning a deposit is an obligation the fund must meet, this borrowing is not limited by the debt fraction that caps margin buys.
- Each loan snapshots the bank's interest rate when it opens, so a later rate change never touches existing loans.
- A loan's term is measured in trading days, set once at origination, and scales with its size against the borrower's worth: a larger loan relative to worth runs more trading days, within a fixed minimum and maximum.
- At the end of each trading day the loan is charged a scheduled principal repayment plus a flat slice of interest, paid from the borrower's cash and recorded on the trader's cash movements. Interest is spread evenly across the term, so repaying the loan on schedule costs the principal plus a fixed percentage of it.
- With more than one loan, cash pays the oldest loan first.
- A missed or partial payment classifies unpaid scheduled principal as overdue principal without adding it to outstanding principal a second time. Unpaid interest becomes overdue interest, and assessed fines accumulate separately as fees.
- Repayment clears assessed fees first, then overdue interest, overdue principal, and finally current principal. Paying overdue principal reduces both its overdue classification and outstanding principal by the same amount. Repaying a loan in full ahead of its term settles only the interest charged so far, so early repayment forgoes the remaining scheduled interest.
- In the final stretch of a loan's term, a borrower still behind on payments is forced to sell shares below the market price to raise cash fast — the same forced-sale mechanism as a bankruptcy, and it applies to the human player too as a deliberate exception, because the player agreed to the loan's terms. This does not wipe the borrower's cash or mark it bankrupt.
- Carrying more debt raises a trader's bankruptcy risk and makes automated traders more willing to sell to deleverage.
- A loan closes when it is fully repaid, or when its borrower leaves the market through bankruptcy, departure, or a fund wind-down — in which case the debt is discharged.
- Total liability is outstanding principal plus overdue interest and assessed fees. Overdue principal is already included in outstanding principal, so net worth, borrowing capacity, distress sizing, and loan totals never count it twice; gross holdings value stays visible separately.
- Existing debt from before this feature is migrated into loans on first launch, leaving no negative balances.

## Where to see loans

- The Banks page lists each bank with its rate, accumulated revenue balance, and current lending book.
- The Bank loans page is a filterable roster of every explicit term loan by bank and status, with overdue principal, overdue interest, and fees shown separately. A note states that margin debit appears on participant and player views.
- The player panel lists the player's own loans with the same arrears breakdown and buttons to repay part or all of a loan.
- Each trader's page shows its loans with the reconciled total liability, and loan activity appears on the trader's cash movements.
