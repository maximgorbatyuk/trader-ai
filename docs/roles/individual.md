# Individual

An Individual is the default automated trader in the simulation. It represents an ordinary market participant with cash, a personality profile, and holdings that can rise or fall in value as the market cycles.

## Rules

- An Individual can buy, sell, hold shares, receive dividends, and carry a negative balance when a buy stays within the market's debt allowance.
- Its automated trading is shaped by temperament and risk profile: aggressive and high-risk traders act more often and in larger sizes, while conservative and low-risk traders wait more often and trade in smaller sizes.
- Each automated decision is one action for the cycle: buy, sell, or wait. It does not place a new order for a company where it already has an open order.
- Buy orders reserve cash at the limit price. Sell orders can list only shares the trader owns and has not already listed in another open sell order.
- Income from sales or dividends first improves a negative balance before it becomes spendable cash.
- Resting orders can be re-priced toward the market and eventually cancelled if they stay open too long.
- A sharp price drop cancels ordinary buy orders for the affected company, and a sharp price rise cancels ordinary sell orders. A stock split cancels the trader's open orders for the split company.
- If an Individual cannot afford the cheapest share for several cycles but still owns shares, it may list part of its most valuable holding to raise cash.
- After the opening protection window, an Individual can go bankrupt when its holdings become extremely valuable or when debt pressure is high. Bankruptcy wipes cash, discharges negative balance, makes the trader inactive, and forces a sell-down of most holdings.
- After the fund-opening window, a low-balance Individual can join an existing collective fund or open a new one. While it is a fund member, the fund-member rules apply.
- A shareless, low-cash Individual that has been unable to buy for a long stretch can leave the market. The game archives the departure and creates a replacement trader.
