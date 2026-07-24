import assert from 'node:assert/strict'
import test, { after, before } from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { createServer } from 'vite'

// The tab strip reads its selection from the `tab` query param; these SSR renders cover which panel is active for
// a given URL. The click-to-push side is react-router's setSearchParams and needs a DOM harness this repo lacks.
let server
let ActorTabs

before(async () => {
  server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  ;({ ActorTabs } = await server.ssrLoadModule('/src/PlayerPanel.jsx'))
})

after(() => server?.close())

function renderTabs(entry, overrides = {}) {
  const props = {
    participantId: 1,
    canCancelOrders: true,
    marketMap: createElement('div', { className: 'market-map-sentinel' }, 'MARKET MAP'),
    orderBook: createElement('div', { className: 'order-book-sentinel' }, 'ORDER BOOK'),
    members: null,
    attention: [],
    openOrders: [],
    loans: [],
    loanStatus: 'active',
    onLoanStatusChange: () => {},
    cashMoves: [],
    settlements: [],
    companies: [],
    participants: [],
    showFavoriteCompanies: true,
    onSelectCompany: () => {},
    onRefresh: () => {},
    ...overrides,
  }
  return renderToStaticMarkup(
    createElement(MemoryRouter, { initialEntries: [entry] }, createElement(ActorTabs, props)),
  )
}

test('the tab query param selects the matching tab panel', () => {
  const markup = renderTabs('/?tab=orders')

  assert.match(markup, /id="playerpanel-orders"/)
  assert.match(markup, /aria-labelledby="playertab-orders"/)
  assert.ok(markup.includes('No open orders.'), 'the open-orders panel body is rendered')
  assert.ok(!markup.includes('market-map-sentinel'), 'the market map is not the active panel')
})

test('an unknown tab param falls back to the market map', () => {
  const markup = renderTabs('/?tab=not-a-real-tab')

  assert.match(markup, /id="playerpanel-map"/)
  assert.ok(markup.includes('market-map-sentinel'), 'the market map is the active panel')
  assert.ok(!markup.includes('No open orders.'), 'the open-orders panel is not rendered')
})

test('no tab param defaults to the market map', () => {
  const markup = renderTabs('/')

  assert.match(markup, /id="playerpanel-map"/)
  assert.ok(markup.includes('market-map-sentinel'), 'the market map is the active panel')
})

test('high-risk attention uses the final audit wording', () => {
  const markup = renderTabs('/?tab=attention', {
    attention: [
      {
        companyId: 1,
        name: 'Acme',
        currentPrice: 10,
        priceChangePct: 0,
        shares: 5,
        marketValue: 50,
        highRisk: true,
      },
    ],
  })

  assert.match(markup, /title="Standing High risk verdict in the last 20 cycles">High risk<\/span>/)
  assert.doesNotMatch(markup, /High or Extra risk/)
})
