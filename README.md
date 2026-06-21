# trader-ai

## Tech stack

- .NET 10
- SQLite database
- Web API for backend
- React for frontend
- Some mechanism for trading simulation - code should be able to run in a loop in parallel thread and make decisions based on the market data (to discuss with AI)

## How to launch

Prerequisites:

- .NET SDK
- Node.js and npm

First-time setup:

```bash
dotnet restore TraderAi/TraderAi.sln
dotnet tool restore
npm --prefix frontend install
```

Start the app:

```bash
./start-dev.sh
```

The frontend runs at `http://127.0.0.1:5173`. The backend runs at `http://127.0.0.1:5100`.

Each participant has its own detail page at `/participants/<id>` — temperament and risk profile (editable), bank balances, holdings valued against current prices, and recent orders, trades, and cash movements. Open it in a separate tab from the Traders table on the dashboard.

The dashboard lists every company with its industry, and a Newswire panel shows the news events the running market publishes on a cycle schedule — some of which nudge a company's or an industry's share price. You can also add a news event by hand from that panel, choosing the target company or industries, a theme, and the impact direction and size.

The market can also be hit by a crisis — a random shock, growing more likely the longer the market runs without one, that drives a few sectors (local) or a large share of all sectors (global) sharply down. A banner highlights a recent crisis and it appears in the Newswire as an alert. A sharp drop, from a crisis or a news event, also cancels the standing buy orders for the affected companies, just as a sharp rise cancels their standing sell orders.

No authentication is required between the frontend and backend for local development.
