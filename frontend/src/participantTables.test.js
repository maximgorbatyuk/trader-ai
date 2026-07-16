import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { createServer } from 'vite'

// Renders a component through Vite's SSR graph (so JSX and bare deps resolve) inside a router, since these
// tables link to detail pages. Vite externalizes react/react-router-dom for SSR, so the router context the
// top-level import provides is the same instance the loaded component consumes.
async function render(t, modulePath, exportName, props) {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const module = await server.ssrLoadModule(modulePath)
  const Component = module[exportName]
  return renderToStaticMarkup(createElement(MemoryRouter, null, createElement(Component, props)))
}

test('IndustryHoldingsTable renders sortable headers and rows in server (paged) mode', async (t) => {
  const markup = await render(t, '/src/IndustryHoldingsTable.jsx', 'IndustryHoldingsTable', {
    rows: [
      { industryId: 1, industryName: 'Alpha', companyCount: 2, shares: 100, value: 5000, costBasis: 4000, pnl: 1000, pct: 0.6 },
      { industryId: 2, industryName: 'Beta', companyCount: 1, shares: 50, value: 3000, costBasis: 3500, pnl: -500, pct: 0.4 },
    ],
    sortKey: 'value',
    sortDir: 'desc',
    onToggleSort() {},
  })

  assert.match(markup, /class="th-sort/) // sortable headers present
  assert.match(markup, /Alpha/)
  assert.match(markup, /Beta/)
  assert.match(markup, /60\.0%/) // pct fraction rendered as a percentage
  assert.match(markup, /\/industries\/1/) // industry links through
})

test('IndustryHoldingsTable groups client-side with plain headers when no sort props are passed', async (t) => {
  const markup = await render(t, '/src/IndustryHoldingsTable.jsx', 'IndustryHoldingsTable', {
    holdings: [{ companyId: 30, shares: 10, marketValue: 100, costBasis: 80 }],
    companies: [{ id: 30, industryId: 1, industryName: 'Alpha' }],
  })

  assert.doesNotMatch(markup, /class="th-sort/) // static headers in client mode
  assert.match(markup, /Industry/)
  assert.match(markup, /Alpha/)
})

test('InvestmentsTable renders sortable headers when sort props are passed', async (t) => {
  const investment = {
    id: 1,
    companyId: 5,
    companyName: 'Acme',
    investorParticipantId: 7,
    investorName: 'Trader',
    dealValue: 1000,
    sharesIssued: 10,
    sharesBeforeDeal: 100,
    investorSharePercent: 5,
    capitalizationBeforeDeal: 1000,
    finalCapitalization: 2000,
    createdInCycleNumber: 3,
    tradingDayNumber: 2,
  }

  const markup = await render(t, '/src/InvestmentsTable.jsx', 'InvestmentsTable', {
    investments: [investment],
    showInvestor: false,
    sortKey: 'dealValue',
    sortDir: 'desc',
    onToggleSort() {},
  })

  assert.match(markup, /class="th-sort/)
  assert.match(markup, /Acme/)
  assert.doesNotMatch(markup, /Investor/) // investor column hidden on a participant page
})

test('InvestmentsTable renders plain headers for its unpaged callers', async (t) => {
  const markup = await render(t, '/src/InvestmentsTable.jsx', 'InvestmentsTable', {
    investments: [{
      id: 1,
      companyId: 5,
      companyName: 'Acme',
      investorParticipantId: 7,
      investorName: 'Trader',
      dealValue: 1000,
      sharesIssued: 10,
      sharesBeforeDeal: 100,
      investorSharePercent: 5,
      capitalizationBeforeDeal: 1000,
      finalCapitalization: 2000,
      createdInCycleNumber: 3,
      tradingDayNumber: 2,
    }],
  })

  assert.doesNotMatch(markup, /class="th-sort/)
  assert.match(markup, /Investor/)
  assert.match(markup, /Acme/)
})
