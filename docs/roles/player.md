# Player

The Player is the human-controlled trader. It uses the same market, order book, holdings, and dividends as automated traders, but no automated market service trades on its behalf.

## Rules

- A market can have at most one Player at a time.
- The Player is created on demand after a market exists and starts with a random ordinary starting balance.
- The Player places buy and sell orders manually and may choose any limit price inside the allowed order range around the LULD reference; a price beyond that range is rejected. An order inside the allowed range but outside the executable band rests and waits for the band to reach it. See [LULD price controls](../rules/luld.md).
- Buy orders reserve cash at the limit price and can use the same margin buying power as other traders. A fill beyond available cash increases a separate margin debit instead of opening an explicit term loan or leaving a negative cash balance.
- Sell orders can list only shares the Player owns and has not already listed.
- Short selling is planned for later and is not implemented.
- The Player cannot place an order opposite to its own open order for the same company, and cannot trade a closed company.
- The Player receives dividends on held shares and sale proceeds like any other share owner.
- The Player may mark companies as favorites from company details. Favorite companies appear in the Player views and can be isolated on the Market Map; the marker remains attached to retained company history until the market resets.
- A fill changes the Player's economic cash and shares immediately and settles on T+1. The Player panel shows total, settled, and pending cash and shares and lists pending settlement instructions.
- The automated decision pass skips the Player.
- The market does not re-price or age out Player orders because they rested too long, but the universal allowed-range check still applies: a Player order left beyond the allowed range after the band moves is cancelled and its reserved cash released.
- News and crisis price shocks do not cancel Player orders. Stock splits remain a market-wide exception that cancels participant orders for the affected company. LULD Limit State and Trading Pause preserve Player orders; they remain cancellable and may execute in the reopening auction.
- The Player manually cancels unwanted open orders. Cancelling a buy releases reserved cash; cancelling a sell frees the listed shares.
- The Player can open and manage one fund, but deposits and withdrawals use only settled, unreserved cash. The fund cannot close while a trade is awaiting settlement or a margin liability remains.
- The Player does not join collective funds, does not go bankrupt, and does not leave through market-exit churn. Persistent term-loan arrears can still trigger a forced sale under the loan terms, and a margin maintenance deficiency can independently create margin-call sell orders.
- Resetting the market clears the Player with the rest of the simulation state.
