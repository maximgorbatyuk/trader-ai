# Margin Accounts

Margin buying uses a participant-level margin account. It is separate from explicit term loans: a purchase beyond available cash increases a revolving margin debit and never originates a bank loan.

## Rules

- Every active participant type, including the Player and Collective Funds, can have one margin account.
- Buying power is based on account equity, existing holdings, current margin liability, the initial margin rate, and cash already reserved by open buy orders.
- The default initial margin rate is 50%. An order may reserve cash and margin buying power only up to the amount available under that requirement.
- When a fill costs more than the buyer's unreserved cash, the shortfall becomes margin debit. The participant's economic cash balance remains non-negative.
- Margin liability is debit balance plus accrued interest. It is reported separately from term-loan liability and is subtracted once when net worth is calculated.
- Interest accrues once when a new trading day starts, using the current debit balance. The one-minute break and additional cycles within the same day do not add interest.
- Sale proceeds pay accrued margin interest first and margin debit second. Any remainder becomes free participant cash.
- The default maintenance requirement is 25% of gross holdings value. If account equity falls below it, the account enters a margin call and the deficiency is shown separately.
- A margin call creates discounted sell orders from settled holdings until the account targets a small buffer above maintenance. These orders use the normal order book and may remain unfilled or partially filled.
- The call is satisfied after equity again meets the maintenance requirement. Margin calls do not turn into term loans and do not use the loan-arrears schedule.
- Short positions, stock borrow, and buy-to-cover orders are planned for later and are not implemented.

## Where to see it

- Every trader detail page shows **Account equity**, **Margin debit**, **Margin interest**, and **Buying power** in its headline statistics.
- Its **Margin account** panel shows **Initial requirement**, **Maintenance requirement**, **Maintenance excess**, **Deficiency**, and **Call status** as **✓ Clear** or **! Open**.
- The player panel shows the same headline figures under **Balances** and the rates, requirements, excess or deficiency, and call status in the **Margin** tab. The selected managed fund has its own account and figures.
- Order forms use buying power when showing how many shares can be bought with margin. Forced-sale orders appear in ordinary open-order and order-book views.
- The **Bank loans** page explicitly contains term loans only; margin debit is displayed on participant and player views instead.

See [Bank loans](bank-loans.md) for the separate fixed-term product and [Trade settlement](settlement.md) for T+1 delivery.
