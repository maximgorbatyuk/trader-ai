# Participant Rules

This page describes a participant from the trader's point of view. It covers what a trader starts with, what it may and may not do, the states it can be in, how it joins a collective fund, and how its buy and sell orders work. For the full data model, see `domain.md`.

## What a trader starts with

- A name, a type, a temperament, and a risk profile.
- A starting cash balance. This is both its initial and its current balance.
- No reserved cash. Reserved cash only appears once it opens a buy order.
- No shares. All shares begin owned by their issuing company until someone buys them.

Starting balances are not uniform. Most traders start with $10,000 to $200,000. About one in seven start as "whales", with up to two billion dollars.

## Trader types

There are four participant types: Individual, Company, AIAgent, and CollectiveFund.

- Individual and AIAgent are the active traders. They make a decision every cycle.
- Company is the share issuer. It is not an automated trader.
- CollectiveFund is a pooled trader. It is not seeded at the start. It is created during the simulation when traders pool together.

## Cash and balances

A trader has three cash figures.

- Current balance is its total cash.
- Reserved balance is cash locked inside its open buy orders.
- Available cash is the current balance minus the reserved balance. This is what it can spend.

## States a trader can be in

A trader has no single status field. Its state is the sum of a few flags.

- **Active.** The default. The trader places orders and is read by the decision engine.
- **Cash-starved.** The trader cannot afford even the cheapest share. A counter climbs each such cycle.
- **In forced liquidation (bankrupt).** A wealthy trader that has collapsed. Its cash is gone and it is selling down holdings. It is no longer active.
- **Fund member.** The trader has joined a collective fund. It stays active but stops bidding for itself. It is hidden from the Traders table while it belongs to the fund, and returns once it leaves or the fund closes.
- **Inactive.** The trader can no longer place orders. A bankrupt trader and a closed fund are inactive.

## What a trader may do

- Place a buy order when it is active and has the cash to reserve.
- Place a sell order when it is active and owns the shares.
- Receive dividends on the shares it holds.
- Join or open a collective fund, if eligible.
- Have its temperament and risk profile changed.

## What a trader may not do

- Buy more than its available cash can reserve.
- Sell shares it does not own.
- Sell shares already committed to another open sell order of its own.
- Place any order while inactive.
- Belong to more than one collective fund at a time.

A fund member also stops placing buy orders. It may still sell shares it already owns.

A collective fund never goes bankrupt. A fund that is winding down places no buy orders.

## How buy orders work

From the trader's side, a buy order works like this.

- The trader bids 1% to 5% above the last price so the order can cross.
- The order reserves cash at creation. The reserved amount is the quantity times the limit price.
- That reserved cash cannot be used by another buy order.
- The order matches a sell order when the bid is at or above the ask.
- A match executes at the midpoint of the two prices. So the trader often pays less than its bid.
- When a fill costs less than was reserved, the unused cash for the filled shares is released.
- The order stays open across cycles until it fills or is cancelled.
- A stale order is nudged up toward the market over later cycles, if the trader can cover the extra reservation.
- An order that rests for about fifteen cycles is cancelled, and its reserved cash is released.
- A sharp price drop, from a crisis or a news event, cancels standing buy orders and releases their cash.

## How sell orders work

- The trader lists specific shares it owns. It asks 1% to 5% below the last price so the order can cross.
- A sell order reserves no cash.
- The same share cannot be listed in two open sell orders at once.
- The order matches a buy order when a bid is at or above the ask.
- A match executes at the midpoint of the two prices.
- The seller is credited the proceeds.
- The order stays open until it fills or is cancelled.
- A stale order is nudged down toward the market over later cycles.
- An order that rests for about fifteen cycles is cancelled, and its shares are freed to be listed again.
- A sharp price rise cancels standing sell orders and frees their shares.

## How an automated trader decides

Each cycle, every active automated trader makes one choice: buy, sell, or do nothing.

- A rising price pulls it toward buying. A falling price pulls it toward selling.
- Strong resting buy demand on a company adds to the buy pull.
- After an extreme run-up, holders take profit. After an extreme drop, others hunt the bargain.
- When no pull dominates, the trader picks at random among the actions open to it.
- Order size comes from a sizer, bounded by what the trader can afford or owns.

## Cash-starved selling

- A trader that cannot afford the cheapest share anywhere is cash-starved that cycle.
- After five straight cash-starved cycles, it raises cash on its own.
- It lists half of its most valuable holding, priced a little below the market so it sells.

## Dividends

- Every share owner is paid a dividend every ten to twenty-five cycles.
- The payout is 0.1% to 2% of each held share's current price.
- It is credited straight to the owner's balance.

## Bankruptcy

Only an Individual or AIAgent can go bankrupt. A fund and a company cannot.

- A trader is at risk only while the market value of its shares stays at or above two billion dollars. Its cash does not matter here.
- No trader is at risk during the market's first 500 cycles.
- After that, each cycle above the line adds 0.2% to its collapse chance, up to a 10% cap.
- A collapse wipes its cash and forces it to sell 65% of its holdings.
- Forced sales start 20% below the market. Unsold orders drop another 5% each cycle until the target is met.
- The bankrupt trader becomes inactive and does not trade again.

## Joining a collective fund

A trader can pool its cash into a collective fund. The rules from the trader's side:

**Who is eligible**

- The trader is an Individual or AIAgent, active, and not bankrupt.
- Its current balance is under $500,000.
- It is not already in a fund.
- No fund is created or joined during the market's first 50 cycles.

**The roll**

- Each cycle, an eligible trader has a 5% base chance to join an existing fund, and a 3% base chance to open a new one.
- A long stretch unable to afford any share raises both chances.
- After 10 such cycles, the join chance gains 20 points and the open chance gains 10 points.
- After 20 such cycles, the join chance gains 40 points and the open chance gains 20 points. This is the cap.

**Joining**

- The trader's open buy orders are cancelled first.
- It contributes 90% of its cash to the fund as a deposit.
- It then stops bidding for itself, but may keep selling shares it already owns.
- It draws a share of the fund's dividends, sized by its deposit. The fund passes through half of each dividend it receives.
- A trader that opens a new fund becomes its first member.
- A fund holds up to 20 members. New joiners go to the emptiest fund.

**Leaving**

- A member must leave once its own balance reaches one hundred million dollars.
- Over the line, its chance to leave starts at 20% and ramps 2 points each cycle to a 90% cap.
- On leaving, its full deposit is returned. If the fund is short on cash, it sells shares to raise it.
- When only two members remain and one leaves, the fund sells everything and splits the proceeds evenly. Both then trade on their own again.
