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
  assert.doesNotMatch(markup, /map-favorite/)
})

test('marks favorite companies with a star badge on the map', async (t) => {
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
      { id: 1, name: 'Acme', issuedSharesCount: 100, currentPrice: 10, industryName: 'Tech', currentRating: null, luldState: null, isFavorite: true },
    ],
    participants: [{ id: 1, currentBalance: 5000 }],
    playerHoldingCompanyIds: new Set(),
    lastDividendTotal: 0,
    currentCycleNumber: 1,
    news: [],
    onSelectCompany() {},
  }))

  assert.match(markup, /class="map-favorite"/)
  assert.match(markup, /Acme,[^"]*· Favorite\. Open details\./)
})

test('shows the Trader AI identity when the market has no companies', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { MarketMapPanel } = await server.ssrLoadModule('/src/MarketMapPanel.jsx')
  const markup = renderToStaticMarkup(createElement(MarketMapPanel, {
    embedded: true,
    companies: [],
    participants: [],
    playerHoldingCompanyIds: new Set(),
    lastDividendTotal: 0,
    currentCycleNumber: null,
    news: [],
    crises: [],
    scienceInvestigations: [],
    onSelectCompany() {},
  }))

  assert.match(markup, /class="market-map-empty"/)
  assert.match(markup, />Trader AI</)
  assert.doesNotMatch(markup, /Seed the market/)
})

test('offers exactly the five final audit statuses in strongest-to-riskiest order', async (t) => {
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
      { id: 1, name: 'Acme', issuedSharesCount: 100, currentPrice: 10, currentRating: 'Stable' },
    ],
    participants: [],
    playerHoldingCompanyIds: new Set(),
    lastDividendTotal: 0,
    currentCycleNumber: 1,
    news: [],
    crises: [],
    scienceInvestigations: [],
    onSelectCompany() {},
  }))

  const auditFilterStart = markup.indexOf('Audit status')
  const auditFilterEnd = markup.indexOf('</select>', auditFilterStart)
  assert.ok(auditFilterStart >= 0)
  assert.ok(auditFilterEnd > auditFilterStart)
  const auditFilterMarkup = markup.slice(auditFilterStart, auditFilterEnd)
  const options = [...auditFilterMarkup.matchAll(/<option value="([^"]+)"[^>]*>([^<]+)<\/option>/g)]
    .map((match) => [match[1], match[2]])
  assert.deepEqual(options, [
    ['all', 'Any rating'],
    ['none', 'No audit'],
    ['ExtraRaisedExpectations', 'Extra raised expectations'],
    ['RaisedExpectations', 'Raised expectations'],
    ['Stable', 'Stable'],
    ['LowRisk', 'Low risk'],
    ['HighRisk', 'High risk'],
  ])
})

test('renders every audit badge with a text label and non-color direction glyph', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { RatingBadge } = await server.ssrLoadModule('/src/RatingBadge.jsx')
  const ratings = [
    ['ExtraRaisedExpectations', '↑↑', 'Extra raised expectations'],
    ['RaisedExpectations', '↑', 'Raised expectations'],
    ['Stable', '→', 'Stable'],
    ['LowRisk', '↓', 'Low risk'],
    ['HighRisk', '↓↓', 'High risk'],
  ]
  const markup = renderToStaticMarkup(createElement(
    'div',
    null,
    ratings.map(([rating]) => createElement(RatingBadge, { key: rating, rating })),
  ))

  for (const [rating, glyph, label] of ratings) {
    assert.match(markup, new RegExp(`data-rating="${rating}"[^>]*>.*aria-hidden="true">${glyph}</span>${label}`))
  }
})
