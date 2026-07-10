# Frontend guidance

The root `AGENTS.md` also applies here. Read `PRODUCT.md` before changing UI behavior or appearance.

## Application shell and routing

- Keep every page beneath the pathless layout rendered by `AppShell`; shared navigation, top bar, footer, market controls, and selected actor stay mounted there.
- `AppShell` owns the shared market poll and exposes market state and actions through outlet context. Dashboard and trade-market pages must not add their own `/market` polling loops.
- A route page renders only `<main className="main">`. Page-specific polling uses an immediate load plus an interval and cleans up on unmount so it remains StrictMode-safe.
- Trader, company, industry, crisis, and player detail surfaces use standalone routes. Point deep links to those routes rather than creating another full-detail modal.

## Shared components

- Reuse `Treemap` for market and industry maps; tile meaning must include a glyph or text in addition to color.
- Keep roster tables presentational. Pages own server-side paging, filters, and sorting; small participant tables use `useClientTable`, `SortHeader`, and `Pager`.
- Reuse `OrderForm`, `PercentButtons`, and `resolveActor` for player and managed-fund trading. Do not fork separate order logic for the two actors.
- Reuse `IndustryHoldingsTable`, `cashMovements.js`, and the established detail components instead of duplicating their calculations or labels.
- The player has dashboard and standalone-page surfaces, not a player modal.

## Product UI

- Preserve the light trading-terminal direction and existing CSS tokens.
- Reserve green and red for market meaning, and never rely on color alone.
- Preserve keyboard focus, roving tab behavior, semantic labels, and reduced-motion alternatives.
- Confirm the exact component when a requested UI element could refer to more than one surface.

## Verification

- Run `rtk npm --prefix frontend run lint` and `rtk npm --prefix frontend run build` after frontend code changes.
