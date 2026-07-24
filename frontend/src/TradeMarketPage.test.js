import assert from 'node:assert/strict'
import test from 'node:test'
import { readFile } from 'node:fs/promises'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter, Outlet, Route, Routes } from 'react-router-dom'
import { createServer } from 'vite'

test('exposes filled orders and investments as tabs without the live order book', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { default: TradeMarketPage } = await server.ssrLoadModule('/src/TradeMarketPage.jsx')
  const shell = createElement(Outlet, { context: { market: null, actorKind: 'player' } })
  const page = createElement(
    MemoryRouter,
    { initialEntries: ['/trade-market'] },
    createElement(
      Routes,
      null,
      createElement(
        Route,
        { element: shell },
        createElement(Route, { path: '/trade-market', element: createElement(TradeMarketPage) }),
      ),
    ),
  )

  const markup = renderToStaticMarkup(page)

  assert.equal(markup.indexOf('Order book'), -1)
  assert.ok(markup.indexOf('Filled orders / settlements') >= 0)
  assert.ok(markup.indexOf('Recent investments') >= 0)
  // The map is the default tab, so its empty state stands in for the active panel body.
  assert.match(markup, /class="market-map-empty"/)
  assert.match(markup, />Trader AI</)
})

test('wires every dashboard and Trade Market news strip to one shared portfolio audit modal', async () => {
  const tradeMarketSource = await readFile(new URL('./TradeMarketPage.jsx', import.meta.url), 'utf8')
  const dashboardSource = await readFile(new URL('./App.jsx', import.meta.url), 'utf8')

  assert.equal(
    (dashboardSource.match(/onSelectPortfolioAuditSummary=\{setSelectedPortfolioAuditSummaryId\}/g) ?? []).length,
    1,
  )
  assert.equal(
    (tradeMarketSource.match(/onSelectPortfolioAuditSummary=\{setSelectedPortfolioAuditSummaryId\}/g) ?? []).length,
    2,
  )
  for (const source of [dashboardSource, tradeMarketSource]) {
    assert.equal((source.match(/<PortfolioAuditSummaryModal/g) ?? []).length, 1)
    assert.match(source, /summaryId=\{selectedPortfolioAuditSummaryId\}/)
    assert.match(source, /onClose=\{\(\) => setSelectedPortfolioAuditSummaryId\(null\)\}/)
  }
})
