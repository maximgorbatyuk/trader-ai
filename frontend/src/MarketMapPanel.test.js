import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'

test('renders the embedded map without the panel card', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { MarketMapPanel } = await server.ssrLoadModule('/src/MarketMapPanel.jsx')
  const markup = renderToStaticMarkup(createElement(MarketMapPanel, {
    embedded: true,
    companies: [
      { id: 1, name: 'Acme', issuedSharesCount: 100, currentPrice: 10, industryName: 'Tech', currentRating: null, luldState: null, isFavorite: false },
    ],
    participants: [{ id: 1, currentBalance: 5000 }],
    playerHoldingCompanyIds: new Set(),
    lastDividendTotal: 0,
    currentCycleNumber: 1,
    news: [],
    onSelectCompany() {},
  }))

  assert.match(markup, /class="map-embedded"/)
  assert.match(markup, /map-embedded-count/)
  assert.match(markup, /1 companies · 100 shares/)
  assert.match(markup, /class="map-filters"/)
  assert.doesNotMatch(markup, /panel-map/)
  assert.doesNotMatch(markup, /<h2>Market map<\/h2>/)
})
