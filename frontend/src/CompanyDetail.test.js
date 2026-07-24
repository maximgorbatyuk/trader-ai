import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { createServer } from 'vite'
import { companyRiskTrendGlyph } from './format.js'

test('uses upward and downward glyphs for improving and worsening company risk', () => {
  assert.equal(companyRiskTrendGlyph('improved'), '▲')
  assert.equal(companyRiskTrendGlyph('worsened'), '▼')
})

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
  const tabKeys = [
    'capitalization',
    'financials',
    'financial-history',
    'management',
    'cash',
    'shareholders',
    'orders',
    'trades',
    'emissions',
    'audits',
    'investments',
    'news',
  ]
  assert.equal(financialsMarkup.match(/role="tabpanel"/g)?.length, tabKeys.length)
  for (const key of tabKeys) {
    const panelTag = financialsMarkup.match(
      new RegExp(`<div[^>]*role="tabpanel"[^>]*id="companypanel-${key}"[^>]*>`),
    )?.[0]
    assert.ok(panelTag, `tabpanel exists for ${key}`)
    assert.match(panelTag, new RegExp(`aria-labelledby="companytab-${key}"`))
    if (key === 'financials') {
      assert.doesNotMatch(panelTag, /\shidden(?:=|>)/)
    } else {
      assert.match(panelTag, /\shidden=""/)
    }
  }
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

test('keeps hidden company tab panels out of the fixed-height flex layout', async () => {
  const css = await readFile(new URL('./App.css', import.meta.url), 'utf8')

  assert.match(
    css,
    /\.main-fill \.tabpanel:not\(\[hidden\]\)\s*\{[^}]*display:\s*flex;/s,
  )
  assert.doesNotMatch(css, /\.main-fill \.tabpanel\s*\{[^}]*display:\s*flex;/s)
})

test('loads company audits only while the audits tab is active', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const apiModule = await server.ssrLoadModule('/src/api.js')
  assert.equal(typeof apiModule.loadCompanyAuditsForActiveTab, 'function')

  const calls = []
  const getAudits = (...args) => {
    calls.push(args)
    return Promise.resolve({ items: [], total: 0, page: 3, pageSize: 6 })
  }

  assert.equal(
    apiModule.loadCompanyAuditsForActiveTab({
      activeTab: 'financials',
      companyId: 7,
      page: 3,
      pageSize: 6,
      getAudits,
    }),
    null,
  )
  assert.deepEqual(calls, [])

  const result = await apiModule.loadCompanyAuditsForActiveTab({
    activeTab: 'audits',
    companyId: 7,
    page: 3,
    pageSize: 6,
    getAudits,
  })
  assert.deepEqual(calls, [[7, 3, 6]])
  assert.deepEqual(result, { items: [], total: 0, page: 3, pageSize: 6 })
})

test('refreshes active history tabs without reissuing the company base load', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const refreshModule = await server.ssrLoadModule('/src/companyDetailRefresh.js')
  assert.equal(typeof refreshModule.refreshCompanyDetailRequests, 'function')

  let baseRequests = 0
  let financialHistoryRequests = 0
  let auditHistoryRequests = 0
  const refresh = (activeTab, includeBase = false) =>
    refreshModule.refreshCompanyDetailRequests({
      activeTab,
      includeBase,
      refreshBase() {
        baseRequests += 11
      },
      refreshFinancialHistory() {
        financialHistoryRequests += 1
      },
      refreshAuditHistory() {
        auditHistoryRequests += 1
      },
    })

  const initialLoad = refresh('capitalization', true)
  assert.ok(initialLoad instanceof Promise)
  await initialLoad
  assert.equal(baseRequests, 11)

  refresh('financial-history')
  refresh('financial-history')
  refresh('audits')

  assert.equal(baseRequests, 11)
  assert.equal(financialHistoryRequests, 2)
  assert.equal(auditHistoryRequests, 1)

  await refresh('financial-history', true)

  assert.equal(baseRequests, 22)
  assert.equal(financialHistoryRequests, 3)
  assert.equal(auditHistoryRequests, 1)
})

test('renders server-paged audit summaries and keeps legacy audits actionable', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const companyDetailModule = await server.ssrLoadModule('/src/CompanyDetail.jsx')
  assert.equal(typeof companyDetailModule.CompanyAuditHistoryPanel, 'function')

  const selected = []
  const history = {
    items: [
      {
        id: 42,
        rating: 'Stable',
        auditorName: 'Northstar Audit',
        evidenceAvailable: true,
        evaluationStartTradingDayNumber: 8,
        evaluationEndTradingDayNumber: 9,
        effectiveTradingDayNumber: 10,
        totalScore: 1,
        adjustedReturnPercent: 2.25,
        maximumAdjustedCycleMovePercent: 3.5,
        latestDividendOutcome: 'Paid',
        dividendCoverageRatio: 2.4,
        industryTrend: 'Rising',
        financialFactors: {
          profitabilityScore: 72,
          profitabilityLevel: 'High',
          stabilityScore: 81,
          financialVolatilityLevel: 'Low',
          closureRiskScore: 16,
          closureRiskLevel: 'Low',
          managementOutlook: 'Positive',
          managementConfidenceScore: 77,
        },
      },
      {
        id: 17,
        rating: 'HighRisk',
        auditorName: 'Legacy Auditor',
        evidenceAvailable: false,
        evaluationStartTradingDayNumber: null,
        evaluationEndTradingDayNumber: null,
        effectiveTradingDayNumber: null,
        totalScore: null,
        latestDividendOutcome: null,
        dividendCoverageRatio: null,
        industryTrend: null,
        financialFactors: null,
      },
    ],
    total: 26,
    page: 2,
    pageSize: 6,
  }

  const markup = renderToStaticMarkup(
    createElement(companyDetailModule.CompanyAuditHistoryPanel, {
      history,
      page: 2,
      onPage() {},
      onSelectAudit(id) {
        selected.push(id)
      },
      loading: false,
      error: null,
    }),
  )

  assert.match(markup, />Audits<\/h2>/)
  assert.match(markup, />Evaluation period</)
  assert.match(markup, />Effective day</)
  assert.match(markup, />Financial indicators</)
  assert.match(markup, />Dividend evidence</)
  assert.match(markup, />Day 8–9</)
  assert.match(markup, />Day 10</)
  assert.match(markup, />Northstar Audit</)
  assert.match(markup, />Stable</)
  assert.match(markup, />High · 72\.00</)
  assert.match(markup, />Volatility </)
  assert.match(markup, />Closure </)
  assert.match(markup, />Low · 16\.00</)
  assert.match(markup, /Paid · 2\.40×/)
  assert.match(markup, />Rising</)
  assert.match(markup, /Evidence unavailable/)
  assert.match(markup, />High risk</)
  assert.equal((markup.match(/data-audit-id="42"/g) ?? []).length, 2)
  assert.equal((markup.match(/data-audit-id="17"/g) ?? []).length, 2)
  assert.match(markup, /aria-label="View audit 42 details"/)
  assert.match(markup, />Page 2 \/ 5</)
  assert.deepEqual(selected, [])
})

test('keeps the audit pager on the page that owns last-known rows after a page request fails', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { CompanyAuditHistoryPanel } = await server.ssrLoadModule('/src/CompanyDetail.jsx')
  const markup = renderToStaticMarkup(
    createElement(CompanyAuditHistoryPanel, {
      history: {
        items: [{
          id: 42,
          rating: 'Stable',
          auditorName: 'Northstar Audit',
          evidenceAvailable: false,
        }],
        total: 26,
        page: 1,
        pageSize: 6,
      },
      page: 2,
      onPage() {},
      loading: false,
      error: 'Could not load page 2.',
      onSelectAudit() {},
    }),
  )

  assert.match(markup, /Showing the last known audit page\./)
  assert.match(markup, />Page 1 \/ 5</)
  assert.doesNotMatch(markup, />Page 2 \/ 5</)
})

test('exposes Audits as the accessible company detail tab', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const companyDetailModule = await server.ssrLoadModule('/src/CompanyDetail.jsx')
  const markup = renderToStaticMarkup(
    createElement(companyDetailModule.CompanyDetailTabs, {
      activeTab: 'audits',
      onTab() {},
      detail: { name: 'Acme', latestFinancial: null },
      prices: [],
      corporateCashMovements: { items: [], total: 0, page: 1, pageSize: 10 },
      corporateCashPage: 1,
      onCorporateCashPage() {},
      corporateCashTableRef: { current: null },
      financialHistory: { items: [], total: 0, page: 1, pageSize: 6 },
      financialHistoryPage: 1,
      onFinancialHistoryPage() {},
      auditHistory: { items: [], total: 0, page: 1, pageSize: 6 },
      auditHistoryPage: 1,
      onAuditHistoryPage() {},
      onSelectAudit() {},
      shareholders: [],
      orders: [],
      trades: [],
      emissions: [],
      investments: [],
      news: [],
      onSelectNews() {},
    }),
  )

  assert.match(
    markup,
    /role="tab" id="companytab-audits" aria-selected="true" aria-controls="companypanel-audits" tabindex="0"/,
  )
  assert.match(
    markup,
    /role="tabpanel" id="companypanel-audits" aria-labelledby="companytab-audits"/,
  )
  assert.doesNotMatch(markup, />Risk ratings</)
})
