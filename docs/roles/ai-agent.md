# AI Agent

An AI Agent is an automated trader role reserved for agent-like market participants. In the current game rules, it follows the same market behavior as an Individual while keeping a distinct role label for future strategy differences.

## Rules

- An AI Agent can buy, sell, hold shares, receive dividends, and use the same margin buying power as other automated traders. Margin debit is separate from explicit term loans.
- Its decisions use temperament and risk profile in the same way as an Individual: risk affects how strongly it reacts to market signals, and temperament affects action frequency and order size.
- It places at most one automated action per cycle: buy, sell, or wait.
- It cannot sell shares it does not own or shares already committed to another open sell order.
- Its fills change economic positions immediately and settle on T+1. Short selling is planned for later and is not implemented.
- Its ordinary orders can be re-priced, cancelled for age, cancelled by price shocks, or cancelled by a stock split.
- LULD Limit State and Trading Pause preserve its resting orders for cancellation or the reopening auction.
- It can become cash-starved and may sell part of its most valuable holding to raise cash.
- It can join or open a collective fund when eligible. While it is a fund member, the fund-member rules apply.
- It can go bankrupt under the same rules as an Individual.
- It can leave the market after a long shareless, low-cash drought or after a severe fund-loss event.
- Replacement traders may enter the market as AI Agents, so this role can appear as the simulation churns even if the initial demo market starts with Individuals.
