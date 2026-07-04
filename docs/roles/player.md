# Player

The Player is the human-controlled trader. It uses the same market, order book, holdings, and dividends as automated traders, but no automated market service trades on its behalf.

## Rules

- A market can have at most one Player at a time.
- The Player is created on demand after a market exists and starts with a random ordinary starting balance.
- The Player places buy and sell orders manually.
- Buy orders reserve cash at the limit price and can use the same debt allowance as other traders.
- Sell orders can list only shares the Player owns and has not already listed.
- The Player receives dividends on held shares and sale proceeds like any other share owner.
- The automated decision pass skips the Player.
- The market does not re-price, age out, or cancel Player orders because they rested too long.
- News and crisis price shocks do not cancel Player orders. A stock split is the exception: it cancels participant orders for the split company, including the Player's, so the book can reform at the split-adjusted price.
- The Player manually cancels unwanted open orders. Cancelling a buy releases reserved cash; cancelling a sell frees the listed shares.
- The Player does not join collective funds, does not go bankrupt, and does not leave through market-exit churn.
- Resetting the market clears the Player with the rest of the simulation state.
