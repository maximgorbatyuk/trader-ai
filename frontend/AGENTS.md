# Frontend guidance

The root `AGENTS.md` also applies here. Read `PRODUCT.md` before changing UI behavior or appearance.

## Application shell and routing

- Keep every page beneath the pathless layout rendered by `AppShell`; shared navigation, top bar, market controls, and selected actor stay mounted there.
- `AppShell` owns the shared market poll and exposes market state and actions through outlet context. Dashboard and trade-market pages must not add their own `/market` polling loops.
- A route page renders only `<main className="main">`. Page-specific polling uses an immediate load plus an interval and cleans up on unmount so it remains StrictMode-safe.
- Trader, company, industry, crisis, and player detail surfaces use standalone routes. Point deep links to those routes rather than creating another full-detail modal.

## Viewport fit and scrolling

- The dashboard is a fixed-height terminal frame: the page itself never grows a vertical scrollbar. Each route fits inside the content area (viewport height minus the top bar) and pushes overflow into a designated inner region, never onto the page.
- A scrolling inner region needs both `overflow-y: auto` and `min-height: 0` on itself and every flex ancestor between it and `.main`; without `min-height: 0` the flex item refuses to shrink and the page scrolls instead.
- Live or paged tables and feeds size their rows with `useFitPageSize` so the list fills the area without a scrollbar; pass `rowSelector`/`headerSelector`/`reserve` when the markup is not a `<table>` (see `NewsPage`).
- Tabbed detail pages use `.main-fill` on `<main>` and let `flex: 1; min-height: 0` cascade to the panel, as in `CompanyDetailPage` and `TraderDetailPage`.
- Document or multi-pane pages follow the `.about-page` pattern: a fixed-height flex column with `overflow: hidden` whose one inner pane scrolls.
- Reuse one of these three patterns for a new page; do not let `.main` grow past the viewport or add a second full-page scroll container.

## Shared components

- Reuse `Treemap` for market and industry maps; tile meaning must include a glyph or text in addition to color.
- Keep roster tables presentational. Pages own server-side paging, filters, and sorting; small participant tables use `useClientTable`, `SortHeader`, and `Pager`.
- Reuse `OrderForm`, `PercentButtons`, and `resolveActor` for player and managed-fund trading. Do not fork separate order logic for the two actors.
- Reuse `IndustryHoldingsTable`, `cashMovements.js`, and the established detail components instead of duplicating their calculations or labels.
- Build new compact dialogs on the shared `Modal` shell (backdrop, Escape, body scroll lock, focus trap); the larger content dialogs (`CompanyModal`, `TradeModal`, `MoneyTransactionModal`) keep their own shells.
- The player has dashboard and standalone-page surfaces, not a player modal.

## Product UI

- Preserve the light trading-terminal direction and existing CSS tokens.
- Reserve green and red for market meaning, and never rely on color alone.
- Preserve keyboard focus, roving tab behavior, semantic labels, and reduced-motion alternatives.
- Confirm the exact component when a requested UI element could refer to more than one surface.

## Verification

- Run `rtk npm --prefix frontend run lint` and `rtk npm --prefix frontend run build` after frontend code changes.
