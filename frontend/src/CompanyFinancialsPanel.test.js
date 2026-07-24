import assert from 'node:assert/strict'
import { after, before, test } from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'

let server
let CompanyFinancialsPanel

before(async () => {
  server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  const companyDetailModule = await server.ssrLoadModule('/src/CompanyDetail.jsx')
  CompanyFinancialsPanel = companyDetailModule.CompanyFinancialsPanel
})

after(() => server?.close())

test('shows operating, balance-sheet, dividend, and derived indicators', () => {
  assert.equal(typeof CompanyFinancialsPanel, 'function')

  const financial = {
    tradingDayNumber: 4,
    moment: 'Midday',
    revenue: 125_000,
    netProfit: 12_000,
    operatingCashFlow: -9_500,
    totalAssets: 240_000,
    totalLiabilities: 90_000,
    totalDebt: 45_000,
    expectedDividendPerShare: 1.25,
    expectedDividendPool: 12_500,
    dividendCoverageRatio: 2.4,
    latestDividend: {
      declaredAmount: 8_000,
      fundedAmount: 6_000,
      fundingOutcome: 'Reduced',
      tradingDayNumber: 3,
    },
    profitabilityScore: 78.2,
    profitabilityLevel: 'High',
    stabilityScore: 82.1,
    financialVolatilityLevel: 'Low',
    closureRiskScore: 48.7,
    closureRiskLevel: 'Medium',
  }

  const markup = renderToStaticMarkup(createElement(CompanyFinancialsPanel, { financial }))

  for (const label of [
    'Revenue',
    'Net profit',
    'Operating cash flow',
    'Total assets',
    'Total liabilities',
    'Total debt',
    'Expected dividend per share',
    'Expected dividend pool',
    'Expected dividend coverage',
    'Last actual dividend outcome',
    'Last actual dividend declared',
    'Last actual dividend funded',
    'Profitability',
    'Stability',
    'Financial volatility',
    'Closure risk',
  ]) {
    assert.match(markup, new RegExp(`>${label}<`))
  }

  assert.match(markup, /Day 4 · Midday/)
  assert.match(markup, /▲.*\+\$12,000\.00/)
  assert.match(markup, /▼.*−\$9,500\.00/)
  assert.match(markup, /\$1\.25 per share/)
  assert.match(markup, /2\.40×/)
  assert.match(markup, />Reduced</)
  assert.match(markup, />High · 78\.20 \/ 100</)
  assert.match(markup, />High · 82\.10 \/ 100</)
  assert.match(markup, />Low</)
  assert.match(markup, />Medium · 48\.70 \/ 100</)

  const expectedIndex = markup.indexOf('Expected dividend pool')
  const actualIndex = markup.indexOf('Last actual dividend outcome')
  assert.ok(expectedIndex >= 0)
  assert.ok(actualIndex > expectedIndex)
})

test('uses an honest unavailable state instead of zero-valued financials', () => {
  assert.equal(typeof CompanyFinancialsPanel, 'function')

  const markup = renderToStaticMarkup(createElement(CompanyFinancialsPanel, { financial: null }))

  assert.match(markup, /Financial reporting is unavailable/)
  assert.match(markup, /No financial snapshot has been recorded for this company\./)
  assert.doesNotMatch(markup, /\$0\.00/)
})
