# Regenerate the README "Website" screenshots

Use this when the UI changes and the images embedded under the `## Website` section of
`README.md` are stale. Paste the whole file to an agent (it assumes the `agent-browser`
skill is available) and let it run.

## Task

Recapture one screenshot per paragraph in the `## Website` section of `README.md`, save them
to `docs/images/`, and confirm each is embedded and current. Drive the running app with
`agent-browser`.

## Before you start

1. The app must be running locally — `./start-dev.sh` from the repo root. The frontend serves
   at `http://localhost:5173` and the backend at `http://localhost:5100` (`127.0.0.1` works too).
2. Confirm both respond before doing anything (a quick `curl` of the frontend root and the
   backend `/market` is enough).
3. The shots need a "lived-in" market. Inspect the backend and, if the state below is missing,
   leave the loop running (it advances on a timer) until it appears:
   - **At least one collective fund** with several members and some holdings. Funds only form
     after the market has run past its early cycles.
   - **A crisis and/or a science event in the Newswire.** The Newswire blends the latest news,
     crises, science events, and bankruptcies into one feed, so a recent one shows there. A
     crisis cannot occur in the early cycles and grows likelier the longer the market runs
     without one — if there are none yet, keep the loop running and re-check.
   - **A rich individual trader** — any `Individual` that holds shares and has some order and
     trade history.

## Capture technique

- **Freeze the market first.** Pause the loop (`POST /market/pause`) so prices and the treemap
  don't shift between framing and capture. **Resume it at the end** (`POST /market/start`).
- Set a desktop width: `agent-browser set viewport 1440 <height>`.
- Navigate and wait for content before shooting, e.g.
  `agent-browser open <url> && agent-browser wait --load networkidle && agent-browser wait --text "<expected text>"`.
- Whole pages: `agent-browser screenshot --full <path>`.
- A framed region (the top of the dashboard, or a single panel): **do not** pass a CSS selector
  to `screenshot` — on these pages it renders blank. Instead measure the region with
  `agent-browser eval` (its page-coordinate top and height), set the viewport height to the
  region's height, `window.scrollTo(...)` to bring it to the top, then take a plain viewport
  `agent-browser screenshot <path>`. The top bar is sticky and overlays the very top of the
  page, so when scrolling a mid-page panel into view, offset the scroll by the top bar's height.
- **Verify every shot** by opening the PNG. Re-frame if it is blank, clipped, or cut mid-row.

## Screenshots to produce

There is one image per paragraph under `## Website`. Re-read that section first; if paragraphs
were added, removed, or reordered, adjust the set to match. Current mapping:

| Paragraph (what it describes) | Open | File | Must show | Framing |
| --- | --- | --- | --- | --- |
| Participant detail page | `/participants/<id>` of a rich `Individual` | `docs/images/participant-page.png` | Identity + stat bar, editable temperament/risk, bank statement, holdings, recent orders, cash movements, trades | Full page |
| The dashboard + Newswire | `/` | `docs/images/dashboard.png` | The market map, the market-activity chart, and the Traders table | Top of the dashboard down to the bottom of the Traders panel — stop before the Companies table |
| Crisis / science / bankruptcy events | `/` | `docs/images/market-events.png` | The Newswire feed with a crisis as a red alert and a science breakthrough as a green one, among ordinary headlines | The Newswire panel, framed just below the top bar |
| Collective fund | `/participants/<id>` of a `CollectiveFund` with members | `docs/images/collective-fund.png` | The green fund status tag and the Fund members list (who joined, the cycle, deposits, payouts) | Full page |

For the events shot, prefer the Newswire panel over the top-of-page crisis/science banner: the
banner only appears for a short window of cycles after an event and is hard to catch, while the
Newswire shows the same events persistently and colour-codes them (crisis red, science green).

### Picking good IDs (they change on every reseed)

- Rich trader: from `GET /participants`, take an `Individual` with the most `sharesOwned`, then
  confirm `/participants/{id}/holdings`, `/orders`, `/share-transactions`, and
  `/money-transactions` are non-empty.
- Good fund: from `GET /participants`, take `CollectiveFund` entries with `holdingsValue > 0`,
  fetch `GET /participants/{id}` for each, and pick the one with the most `collectiveFundMembers`.

## Embed and finish

1. Place each image directly under its paragraph as Markdown:
   `![<accurate alt text>](docs/images/<file>.png)`. Keep the alt text faithful to what the
   image actually shows; if you reframe a shot, update its alt text to match.
2. Leave any unrelated image already in `docs/images/` untouched unless a paragraph needs it.
3. Resume the loop (`POST /market/start`) and close the browser (`agent-browser close`).
4. Do not commit unless asked.
