# Product

## Users

A single developer — the person building the simulation — using the dashboard during active development. There is no external user base and no authentication. The context is a build-time observability and control panel: kept open while iterating on the backend, used to watch the market move and to drive it. The primary jobs are equally weighted — **observe** (prices, trades, open orders, participant balances, agent behavior across cycles) and **control** (seed the demo market, start/pause the loop, advance cycles, run decisions, place manual orders). Both must be effortless without switching modes.

## Product Purpose

Trader AI simulates a trading market: participants (individuals, companies, AI agents) place buy and sell orders across discrete market cycles; a matching engine fills them by price-time priority, transfers per-share ownership, settles cash, and snapshots prices. A rule-based decision engine trades most participants on its own, and an operator can convert a trader into an AI agent whose orders come from a hosted language model (GLM or MiniMax) running alongside the market. The dashboard is the window into that running simulation — it makes the live state legible at a glance and exposes the controls to step or run the market. Success is when the operator can tell, in seconds, what the market is doing and intervene with confidence.

## Brand Personality

Trading-terminal in a light schema: dense, precise, data-first, and credible. Three words: **precise, dense, trustworthy**. The feel is a professional trading desk rendered in daylight — high information-per-screen and tight rhythm, but reading as calm and legible rather than dark or aggressive. It simulates a financial market, so it should feel financially credible: numbers are the content and deserve real typographic care (alignment, tabular figures, decisive hierarchy). No persuasion, no hype, no decoration for its own sake.

## Anti-references

- **Generic SaaS dashboard template** — warm cream/off-white body, rounded cards everywhere with soft drop shadows, gradient "hero metric" tiles, and tiny tracked-uppercase eyebrows above every section. The default AI-dashboard look.
- **Crypto-casino hype** — neon gradients, glowing buttons, gambling energy, breathless copy.
- **Toy / playful** — cartoonish shapes, oversized rounding, emoji-heavy, anything that undercuts the credibility of a financial market.
- **Marketing landing page** — a giant hero headline, persuasion copy, decorative scroll sections. This is a tool, not a pitch.

## Design Principles

- **Numbers are the interface.** Prices, balances, quantities, and fills are the primary content. Give them tabular alignment, a clear numeric hierarchy, and enough contrast to read instantly — never let chrome out-shout the data.
- **Density without clutter.** Show a lot of live state per screen, but keep every region scannable. Earn density through rhythm and alignment, not by cramming.
- **Reading and acting are one motion.** Observing the market and controlling it happen in the same view. Controls sit where the eye already is; no mode switch between watching and intervening.
- **Truthful, live state.** The UI polls a real backend, so it must tell the truth about connection status, empty states, in-flight actions, and errors. An honest "nothing yet" beats a fabricated-looking placeholder.
- **Credible over flashy.** Trust comes from precision and restraint. Motion and color exist to signal change in the market (a fill, a price move), not to entertain.

## Accessibility & Inclusion

Hold the whole UI to **WCAG 2.1 AA**: body text ≥ 4.5:1 and large text ≥ 3:1 against its background (including muted secondary text and placeholders), visible keyboard focus on every interactive control, and semantic structure for status/landmarks. Honor `prefers-reduced-motion` with a crossfade or instant alternative for every animation. Do not rely on color alone to distinguish buy/sell or positive/negative — pair it with text or shape.
