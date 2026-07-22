# Big investment

A big investment lets a participant or fund fund a company directly: the investor buys a large block of freshly
minted shares at the current price, and the company hands them over in a single completed deal rather than through
the order book.

## Rules

- Any active Individual or Collective Fund can be selected by the automated roll; AI Agents and the human player
  are excluded from it. An AI Agent can invest only through an explicit provider decision when its current market
  snapshot advertises an eligible opportunity, while the human player invests only through the manual action.
- On an ordinary cycle, the market has a single base chance that one deal happens. When it fires, one eligible
  investor and company are chosen.
- An Extra raised-expectations rating gives that company one targeted investment opportunity on the following
  cycle. The targeted opportunity replaces the ordinary roll for that cycle and does not repeat while the rating
  remains current.
- One eligible investor-company pair is chosen for the targeted opportunity. Its chance starts at the ordinary
  base chance when the investor can fund exactly the minimum deal and rises in proportion to additional spendable
  cash, but it can never exceed 50%.
- The invested cash must be at least 40% of the target company's capitalisation, and the investor must be able to
  fund it from settled, unreserved cash. The same spendable-cash amount controls the targeted opportunity's chance.
- At most one automated big investment can happen in a cycle. If the targeted opportunity has no eligible pair or
  its roll misses, no automated deal happens that cycle.
- The deal executes at the company's current price. The invested cash mints that many new shares, expanding the
  company's issued share count.
- The investor receives the new shares immediately as a settled holding and pays the cash immediately; the company
  receives the cash as corporate cash.
- Capitalisation is re-recorded at the unchanged per-share deal price against the enlarged share count; this snapshot alone does not move the per-share price.
- An attached raised-expectations rating then requests an 8% price increase. LULD can clamp the realized move, and the rating impact preserves resting orders rather than cancelling stale orders.
- The company's industry sentiment ticks up by a small amount.
- The company is shielded from delisting for the next few trading days.
- Each deal is announced on the Newswire without a separate price impact of its own.

## Manual action

The player and the player-managed fund can start a deal from the company detail page. The same minimum (40% of
capitalisation) and cash checks apply, and the deal runs through the same executor as the automated roll.

## Recorded facts

Each deal is stored as its own record: the investor, deal value, shares issued, shares and capitalisation before
the deal, the capitalisation after it (deal price × the enlarged share count), the investor's resulting stake, and
the trading day and cycle it happened in. The record survives a departed investor or a closed company.

## Where to see it

- A dedicated **Investments** block lists these deals on three surfaces: **investments received** on a company's
  detail page, **investments made** on a participant's or fund's page, and a market-wide **recent investments**
  block on the Trade Market page.
- The company detail page also records the deal as a filled buy order, a settled trade, and a **Big investment**
  credit in the **Corporate cash movements** panel, and shows the raised-expectations rating and the enlarged share
  count.
- The deal appears on the **Newswire** as an **Investment** item naming the investor, company, share count, and size.

See [Corporate cash](corporate-cash.md) for the issuer cash credit and [Company](../roles/company.md) for the
delisting-protection window.
