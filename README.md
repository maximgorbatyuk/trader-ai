# Trader Simulator

## How to launch

```bash
./start-dev.sh
```

The script will ensure the .NET SDK is installed, Node.js and npm, and then restore the project dependencies. The only thing you have to do - install the [.NET Sdk for you OS](https://dotnet.microsoft.com/download).

## Website

The frontend runs at `http://localhost:5173`. The backend runs at `http://localhost:5100`.

Each trader has a detail block on the Traders page at `/traders?trader=<id>` — temperament and risk profile (editable), bank balances, holdings valued against current prices, and recent orders, trades, and cash movements. Open it from the Traders table on the dashboard.

![A participant's detail page: cash balances, editable temperament and risk, holdings valued at current prices, and recent orders, trades, and cash movements.](docs/images/participant-page.png)

The dashboard's market map is sized by company capitalisation, with the two most recent headlines beneath it. The News page collects the full feed of events the running market publishes on a cycle schedule — some of which nudge a company's or an industry's share price — and is where you can add a news event by hand, choosing the target company or industries, a theme, and the impact direction and size. A separate Trade market page pairs the same market map with an orders-per-cycle activity chart.

![The dashboard: the market map sized by company capitalisation, the market-activity chart, and the Traders table.](docs/images/dashboard.png)

The market can also be hit by a crisis — a random shock, growing more likely the longer the market runs without one, that drives a few sectors (local) or a large share of all sectors (global) sharply down. The shock leaves a temporary risk-off interval that changes sentiment and several participant and market-event probabilities; see [Market crises](docs/logic/crisis.md) for the complete lifecycle. A banner highlights a recent crisis and it appears in the Newswire as an alert. A sharp drop, from a crisis or a news event, also cancels the standing buy orders for the affected companies, just as a sharp rise cancels their standing sell orders. The upbeat counterpart is a science investigation — a small, local breakthrough that lifts a few sectors, shown with its own green banner and Newswire items, and which only nudges prices up without touching the order book. A trader whose share holdings grow very valuable can also go bankrupt: its cash is wiped and most of its holdings are dumped onto the market at a steepening discount until they sell, an event that shows up in the Newswire without moving any other prices.

![The Newswire feed: a crisis shows as a red alert and a science breakthrough as a green one, between ordinary headlines.](docs/images/market-events.png)

Cash-strapped traders may instead pool into a collective fund, which trades as its own participant and is tagged with a green label in the Traders table. A member contributes most of its cash, stops bidding on its own, and earns a share of the fund's dividends. Voluntary departure is locked for seven trading days; one day before eligibility an AI fund raises its normal 10% cash buffer toward 15% so the returned deposit is less likely to require emergency borrowing. Once only two members remain and one departs, the fund sells out and splits the proceeds between them. A member drops out of the Traders table while it belongs to a fund and returns once it leaves or the fund closes. A fund's page lists who has joined and when.

![A collective fund's page: its green status tag and the Fund members who have joined, with the cycle each joined, deposits, and payouts.](docs/images/collective-fund.png)

Traders that run out of road eventually leave the market for good, keeping it churning rather than filling up with stuck, broke participants. A fund that sits unable to trade for long enough unwinds on its own; a trader left with no shares and no cash to buy any may quit after a long drought, its odds climbing the longer it stays stuck; and a fund member handed back only a fraction of what it put in gets one chance to walk away. While a crisis is active these traders bail faster — a global crisis makes each of them far likelier to quit that cycle, a local one somewhat likelier. Every departure is filled by a fresh replacement with a new random balance, so the market holds its size. The Companies roster can switch between active and closed companies, while the Traders roster can switch among active traders, departed traders, and closed funds. The departed-trader archive records each reason for leaving, the cycles joined and left, orders placed, and the final balance compared with the starting position.

You can also step into the market yourself. Join as a human player and you are handed a random starting balance, then trade by hand under the same rules as everyone else — your buy and sell orders reserve cash and match just like theirs, and you collect dividends on the shares you hold. The difference is that the market does not manage your orders day to day: they are never re-priced, never cancelled for resting too long, and never swept away by a crisis or a news event, so you cancel the ones you no longer want yourself. Like every participant, your orders must sit within the allowed price range around the reference — you can rest a buy or sell anywhere in it, but continuous matching only crosses the narrower executable band, and an order left outside the allowed range after the band moves is cancelled. Stock splits still clear participant orders so the book can reform safely. LULD price controls instead preserve resting orders through a limit state and trading pause, then use them in a deterministic reopening auction. A player never goes bankrupt and never joins a collective fund, and a market holds at most one player at a time. A player panel on the dashboard shows settled and pending balances, margin standing, settlements, performance, active assets, orders, cash movements, and term loans. Dedicated Player stats and Fund stats pages in the sidebar open the same full participant view used by automated traders. You place orders from an active company's own view.

The market clock groups 210 two-second cycles into a seven-minute trading day, followed by a separate one-minute break. Trades affect economic cash, ownership, and price immediately, then settle on the next trading day; trader views separate settled and pending cash and shares. Selling issuer float credits the company's own cash at settlement. The simulation also credits independently randomized operating income from an external economy during dividend windows, and dividends can spend only the issuer cash available after that credit. See [Corporate cash](docs/logic/corporate-cash.md).

Buying beyond available cash uses a separate margin account instead of opening a term loan or leaving a negative balance. Margin screens show buying power, debit, interest, maintenance standing, and open calls; a deficient account can place forced-sale orders from settled holdings. Explicit bank loans remain a different fixed-term product. Their screens separate overdue principal, overdue interest, and assessed fees, while total liability counts overdue principal only once. Short selling is planned for later and is not implemented.

No authentication is required between the frontend and backend for local development.

## Documentation

| Page | What it covers |
| --- | --- |
| [Architecture](docs/architecture.md) | The system boundaries and durable patterns connecting the frontend, market cycle, trading, lifecycle, and persistence subsystems. |
| [Domain](docs/domain.md) | The simulation's data model and the core market rules. |
| [Participant rules](docs/participant-rules.md) | Shared participant rules and links to each role page. |
| [Individual](docs/roles/individual.md) | Rules for the default automated trader. |
| [AI Agent](docs/roles/ai-agent.md) | Rules for provider-backed traders driven by a hosted language model. |
| [Player](docs/roles/player.md) | Rules for the human-controlled trader. |
| [Company](docs/roles/company.md) | Rules for issuers and listed assets. |
| [Collective Fund](docs/roles/collective-fund.md) | Rules for pooled fund traders. |
| [Fund Member](docs/roles/fund-member.md) | Rules for traders while they belong to a fund. |
| [Auditors](docs/roles/auditors.md) | Rules for the rating agencies that review companies. |
| [Share price formation](docs/rules/share-price-formation.md) | How company share prices form during each market cycle. |
| [Trading days](docs/rules/trading-days.md) | How 210 trading cycles form a seven-minute day followed by a separate one-minute break. |
| [LULD price controls](docs/rules/luld.md) | How rolling price bands, limit states, trading pauses, and reopening auctions work. |
| [Trade settlement](docs/logic/settlement.md) | How economic positions and T+1 settled cash and shares are kept separate. |
| [Margin accounts](docs/logic/margin.md) | How buying power, margin debit, interest, calls, and forced sales work. |
| [Market crises](docs/logic/crisis.md) | How local and global crises trigger, shock industries, alter market behavior, and record consequences. |
| [Corporate cash](docs/logic/corporate-cash.md) | How primary proceeds and simulated operating income fund issuer cash and dividends. |
| [Sector sentiment](docs/logic/sector-sentiment.md) | How sector confidence shapes shocks and automated trading demand. |
| [Free-share emission](docs/logic/free-share-emission.md) | How large companies issue free shares to dilute price. |
| [Big investment](docs/logic/big-investment.md) | How a participant or fund funds a company by buying newly minted shares. |
| [Bank loans](docs/logic/bank-loans.md) | How explicit fixed-term loans are originated, serviced, and reconciled separately from margin. |
| [Fund advertising](docs/logic/fund-advertising.md) | How the player-managed fund pays to raise its Popularity Index and attract joiners. |
| [Behavioural audit](docs/logic/behavioral-audit.md) | How the thirty-cycle audit scores trading activity and reclassifies the player and their fund. |

## Tech stack

- .NET 10
- SQLite database
- Web API for backend
- React for frontend
- Some mechanism for trading simulation - code should be able to run in a loop in parallel thread and make decisions based on the market data (to discuss with AI)
