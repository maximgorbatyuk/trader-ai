import assert from 'node:assert/strict'
import { after, before, test } from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'

let server
let CompanyFinancialHistoryPanel
let loadFinancialHistoryForActiveTab

before(async () => {
  server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  const historyModule = await server.ssrLoadModule('/src/CompanyFinancialHistoryPanel.jsx')
  const apiModule = await server.ssrLoadModule('/src/api.js')
  CompanyFinancialHistoryPanel = historyModule.CompanyFinancialHistoryPanel
  loadFinancialHistoryForActiveTab = apiModule.loadFinancialHistoryForActiveTab
})

after(() => server?.close())

function historyItem({
  id,
  moment,
  revenue,
  previousRevenue,
  revenueDelta,
  revenueDeltaPercent,
  closureRiskScore,
  previousClosureRiskScore,
  closureRiskDelta,
  closureRiskDeltaPercent,
}) {
  return {
    current: {
      id,
      tradingDayNumber: 1,
      createdInCycleId: id,
      moment,
      revenue,
      netProfit: 98_765,
      closureRiskScore,
    },
    previous:
      previousRevenue == null
        ? null
        : {
            revenue: previousRevenue,
            netProfit: 97_000,
            closureRiskScore: previousClosureRiskScore,
          },
    absoluteDelta:
      revenueDelta == null
        ? null
        : {
            revenue: revenueDelta,
            netProfit: 1_765,
            closureRiskScore: closureRiskDelta,
          },
    percentageDelta:
      revenueDeltaPercent == null
        ? null
        : {
            revenue: revenueDeltaPercent,
            netProfit: 1.82,
            closureRiskScore: closureRiskDeltaPercent,
          },
  }
}

const items = [
  historyItem({
    id: 3,
    moment: 'Midday',
    revenue: 150,
    previousRevenue: 100,
    revenueDelta: 50,
    revenueDeltaPercent: 50,
    closureRiskScore: 42,
    previousClosureRiskScore: 40,
    closureRiskDelta: 2,
    closureRiskDeltaPercent: 5,
  }),
  historyItem({
    id: 2,
    moment: 'DayOpening',
    revenue: 100,
    previousRevenue: 100,
    revenueDelta: 0,
    revenueDeltaPercent: 0,
    closureRiskScore: 40,
    previousClosureRiskScore: 40,
    closureRiskDelta: 0,
    closureRiskDeltaPercent: 0,
  }),
  historyItem({
    id: 1,
    moment: 'Seed',
    revenue: 100,
    previousRevenue: null,
    revenueDelta: null,
    revenueDeltaPercent: null,
    closureRiskScore: 40,
    previousClosureRiskScore: null,
    closureRiskDelta: null,
    closureRiskDeltaPercent: null,
  }),
]

const history = {
  items,
  total: 7,
  page: 1,
  pageSize: 3,
}

test('loads only the selected history tab and requests the active server page', async () => {
  assert.equal(typeof loadFinancialHistoryForActiveTab, 'function')
  const calls = []
  const getFinancials = (...args) => {
    calls.push(args)
    return Promise.resolve(history)
  }

  const skipped = loadFinancialHistoryForActiveTab({
    activeTab: 'financials',
    companyId: 12,
    page: 2,
    pageSize: 6,
    getFinancials,
  })
  assert.equal(skipped, null)
  assert.deepEqual(calls, [])

  const result = await loadFinancialHistoryForActiveTab({
    activeTab: 'financial-history',
    companyId: 12,
    page: 2,
    pageSize: 6,
    getFinancials,
  })
  assert.equal(result, history)
  assert.deepEqual(calls, [[12, 2, 6]])
})

test('shows newest-first checkpoints, server paging, selected metric values, and deltas', () => {
  assert.equal(typeof CompanyFinancialHistoryPanel, 'function')

  const markup = renderToStaticMarkup(
    createElement(CompanyFinancialHistoryPanel, {
      history,
      page: 1,
      onPage() {},
    }),
  )

  assert.match(markup, /<label[^>]*for="financial-history-metric"[^>]*>Metric<\/label>/)
  assert.match(markup, /<option value="revenue" selected="">Revenue<\/option>/)
  assert.match(markup, /role="img" aria-label="Revenue history"/)
  assert.doesNotMatch(markup, /\$98,765\.00/)

  const middayIndex = markup.indexOf('Day 1 · Midday')
  const openingIndex = markup.indexOf('Day 1 · Opening')
  const seedIndex = markup.indexOf('Day 1 · Seed')
  assert.ok(middayIndex >= 0)
  assert.ok(openingIndex > middayIndex)
  assert.ok(seedIndex > openingIndex)

  for (const heading of ['Current', 'Previous', 'Absolute delta', 'Percentage delta']) {
    assert.match(markup, new RegExp(`>${heading}<`))
  }
  assert.match(markup, />\$150\.00</)
  assert.match(markup, />\$100\.00</)
  assert.match(markup, />\+\$50\.00</)
  assert.match(markup, />\+50\.00%</)
  assert.match(markup, />Unchanged</)
  assert.match(markup, />\$0\.00</)
  assert.match(markup, />0\.00%</)
  assert.match(markup, /Page 1 \/ 3/)
})

test('plots only the initially selected metric and labels its unit', () => {
  const markup = renderToStaticMarkup(
    createElement(CompanyFinancialHistoryPanel, {
      history,
      page: 1,
      onPage() {},
      initialMetric: 'closureRiskScore',
    }),
  )

  assert.match(markup, /<option value="closureRiskScore" selected="">Closure risk score<\/option>/)
  assert.match(markup, /role="img" aria-label="Closure risk score history"/)
  assert.match(markup, />Score<\/text>/)
  assert.match(markup, />42\.00</)
  assert.doesNotMatch(markup, /\$150\.00/)
})

test('renders loading, error, empty, and last-known-data states honestly', () => {
  const emptyHistory = { items: [], total: 0, page: 1, pageSize: 6 }
  const loadingMarkup = renderToStaticMarkup(
    createElement(CompanyFinancialHistoryPanel, {
      history: emptyHistory,
      loading: true,
      page: 1,
      onPage() {},
    }),
  )
  assert.match(loadingMarkup, /aria-busy="true"/)
  assert.match(loadingMarkup, /Loading financial history/)

  const errorMarkup = renderToStaticMarkup(
    createElement(CompanyFinancialHistoryPanel, {
      history: emptyHistory,
      error: 'History unavailable.',
      page: 1,
      onPage() {},
    }),
  )
  assert.match(errorMarkup, /role="alert"/)
  assert.match(errorMarkup, /Couldn&#x27;t load financial history/)
  assert.match(errorMarkup, /History unavailable\./)

  const emptyMarkup = renderToStaticMarkup(
    createElement(CompanyFinancialHistoryPanel, {
      history: emptyHistory,
      page: 1,
      onPage() {},
    }),
  )
  assert.match(emptyMarkup, /No financial snapshots have been recorded/)

  const lastKnownMarkup = renderToStaticMarkup(
    createElement(CompanyFinancialHistoryPanel, {
      history,
      error: 'Refresh failed.',
      page: 1,
      onPage() {},
    }),
  )
  assert.match(lastKnownMarkup, /Showing last known financial history/)
  assert.match(lastKnownMarkup, /Refresh failed\./)
  assert.match(lastKnownMarkup, /Day 1 · Midday/)
})
