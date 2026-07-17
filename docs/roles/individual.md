# Individual

An Individual is the default automated trader in the simulation. It represents an ordinary market participant with cash, a personality profile, and holdings that can rise or fall in value as the market cycles.

## Rules

- An Individual can buy, sell, hold shares, and receive dividends. Its automated Low- and Medium-risk buys use cash only; a High-risk Individual may use margin, with total margin liability capped at 10% of net worth. A margin fill increases a separate margin debit, not an explicit term loan.
- Its automated trading is shaped by temperament and risk profile: aggressive and high-risk traders act more often and in larger sizes, while conservative and low-risk traders wait more often and trade in smaller sizes.
- Each automated decision is one action for the cycle: buy, sell, or wait. It does not place a new order for a company where it already has an open order, and order validation prevents opposite-side interest from the same owner from crossing.
- Buy orders reserve cash at the limit price. Sell orders can list only shares the trader owns and has not already listed in another open sell order.
- Automated buys target a soft share-exposure band based on risk: 20–35% of net worth for Low, 35–55% for Medium, and 50–70% for High. Existing open-buy reservations reduce the remaining headroom; above the upper bound the trader does not create another discretionary buy.
- Below the lower bound, buy pull increases and the trader prefers the best residual executable ask, bidding at that exact price. If a company has no remaining sell interest, the trader can instead pass a configurable buy-chance gate and place a small in-band bid above market; its temperament selects the lower, middle, or upper third of the configured premium range. Earlier allocated demand keeps its price-time priority, so a later generated order cannot jump it. See [Participant rules](../participant-rules.md) for the shared size and exposure limits.
- Discretionary sells retain the wider personality-driven pricing behavior, including occasional limits in the allowed waiting range outside the executable band. Every order must stay inside the allowed range. See [LULD price controls](../rules/luld.md).
- A completed trade changes the economic position immediately and settles cash and share delivery on T+1. The trader may resell a same-day purchase, while settled and pending quantities remain distinct.
- Short selling is planned for later and is not implemented.
- Loans are serviced from available, unreserved cash; assessed fees, overdue interest, and principal are allocated separately so the remaining liability reconciles with net worth.
- Margin debit accrues interest once per trading day. Sale proceeds repay margin interest and debit before becoming free cash, and a maintenance deficiency can create forced-sale orders from settled holdings.
- Resting orders can be re-priced toward the executable band — clamped so an aged limit never compounds past the band — and eventually cancelled for age; any order left beyond the allowed range after the band moves is cancelled with its reserved cash released.
- A sharp price drop cancels ordinary buy orders for the affected company, and a sharp price rise cancels ordinary sell orders. A stock split cancels the trader's open orders for the affected company. LULD Limit State and Trading Pause instead preserve open orders for cancellation or the reopening auction.
- If an Individual cannot afford the cheapest share for several cycles but still owns shares, it may list part of its most valuable holding to raise cash.
- After the opening protection window, an Individual can go bankrupt when its holdings become extremely valuable or when debt pressure is high. Bankruptcy wipes cash, discharges open loans, makes the trader inactive, and forces a sell-down of most holdings.
- After the fund-opening window, a low-balance Individual can join an existing collective fund or open a new one. While it is a fund member, the fund-member rules apply.
- A shareless, low-cash Individual that has been unable to buy for a long stretch can leave the market, and the game archives the departure. Separately, while the active-trader roster — Individuals, AI Agents, and Collective Funds together — sits below its maximum, a new rule-based Individual can appear each cycle, seeded with the same starting balance, temperament, and risk spread as a market-seeded trader. Departures and appearances are independent rolls, so the roster drifts up and down rather than staying fixed.
- An operator can convert an Individual into a provider-backed AI Agent from the trader detail page; until then it trades by the rule-based logic above. See [AI Agent](ai-agent.md).
