import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { createServer } from 'vite'

test('renders compact ownership metrics before the paginated shareholder table', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const companyDetailModule = await server.ssrLoadModule('/src/CompanyDetail.jsx')
  assert.equal(typeof companyDetailModule.ShareholdersPanel, 'function')

  const shareholders = Array.from({ length: 11 }, (_, index) => ({
    ownerId: index + 1,
    ownerName: `Owner ${index + 1}`,
    shares: index + 1,
    pctOfIssued: (index + 1) / 1_000,
    marketValue: (index + 1) * 25,
  }))
  const detail = {
    issuedSharesCount: 1_000,
    sharesHeldByIssuer: 100,
    sharesOutstanding: 900,
    shareholderCount: 11,
  }

  const markup = renderToStaticMarkup(
    createElement(
      MemoryRouter,
      null,
      createElement(companyDetailModule.ShareholdersPanel, { shareholders, detail }),
    ),
  )
  const ownershipIndex = markup.indexOf('class="ownership-summary"')
  const tableIndex = markup.indexOf('class="tbl-wrap"')
  const pagerIndex = markup.indexOf('class="pager"')
  const metrics = markup.match(/class="ownership-metric"/g) ?? []
  const renderedOwners = markup.match(/class="cell-link"/g) ?? []

  assert.equal(metrics.length, 5)
  // The shareholder page size now adapts to viewport height, so page one shows a fit-dependent subset rather
  // than a fixed ten; assert the table paged (fewer than all eleven owners) instead of a specific count.
  assert.ok(renderedOwners.length > 0 && renderedOwners.length < 11)
  assert.match(markup, /class="ownership-metrics"/)
  assert.match(markup, />Owner 11<\/a>/)
  assert.doesNotMatch(markup, />Owner 1<\/a>/)
  assert.ok(ownershipIndex >= 0)
  assert.ok(tableIndex > ownershipIndex)
  assert.ok(pagerIndex > tableIndex)
})

test('renders financials and management outlook as independent accessible tabs', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const companyDetailModule = await server.ssrLoadModule('/src/CompanyDetail.jsx')
  assert.equal(typeof companyDetailModule.CompanyDetailTabs, 'function')

  const commonProps = {
    onTab() {},
    detail: {
      name: 'Acme',
      latestFinancial: {
        tradingDayNumber: 4,
        moment: 'Midday',
        revenue: 125_000,
        netProfit: 12_000,
        operatingCashFlow: 9_500,
        totalAssets: 240_000,
        totalLiabilities: 90_000,
        totalDebt: 45_000,
        expectedDividendPerShare: 1.25,
        expectedDividendPool: 12_500,
        dividendCoverageRatio: 2.4,
        latestDividend: null,
        businessRiskScore: 28.5,
        managementRevenueForecast: 136_000,
        managementProfitForecast: 13_500,
        managementOperatingCashFlowForecast: 10_200,
        managementOutlook: 'Positive',
        managementConfidenceScore: 76.4,
        profitabilityScore: 78.2,
        profitabilityLevel: 'High',
        stabilityScore: 82.1,
        financialVolatilityLevel: 'Low',
        closureRiskScore: 18.7,
        closureRiskLevel: 'Low',
      },
    },
    prices: [],
    corporateCashMovements: { items: [], total: 0, page: 1, pageSize: 10 },
    corporateCashPage: 1,
    onCorporateCashPage() {},
    corporateCashTableRef: { current: null },
    shareholders: [],
    orders: [],
    trades: [],
    emissions: [],
    ratings: [],
    investments: [],
    news: [],
    onSelectNews() {},
  }

  const financialsMarkup = renderToStaticMarkup(
    createElement(companyDetailModule.CompanyDetailTabs, {
      ...commonProps,
      activeTab: 'financials',
    }),
  )
  assert.match(
    financialsMarkup,
    /role="tab" id="companytab-financials" aria-selected="true" aria-controls="companypanel-financials" tabindex="0"/,
  )
  assert.match(
    financialsMarkup,
    /role="tab" id="companytab-management" aria-selected="false" aria-controls="companypanel-management" tabindex="-1"/,
  )
  assert.match(
    financialsMarkup,
    /role="tabpanel" id="companypanel-financials" aria-labelledby="companytab-financials"/,
  )
  assert.match(financialsMarkup, />Financials<\/h2>/)
  assert.doesNotMatch(financialsMarkup, />Management outlook<\/h2>/)

  const managementMarkup = renderToStaticMarkup(
    createElement(companyDetailModule.CompanyDetailTabs, {
      ...commonProps,
      activeTab: 'management',
    }),
  )
  assert.match(
    managementMarkup,
    /role="tab" id="companytab-management" aria-selected="true" aria-controls="companypanel-management" tabindex="0"/,
  )
  assert.match(managementMarkup, />Management outlook<\/h2>/)
  assert.doesNotMatch(managementMarkup, />Financials<\/h2>/)
})
