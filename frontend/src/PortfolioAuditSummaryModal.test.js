import assert from 'node:assert/strict'
import test, { after, before } from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { createServer } from 'vite'

let server
let PortfolioAuditSummaryContent
let PortfolioAuditSummaryModal

before(async () => {
  server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  try {
    ;({ PortfolioAuditSummaryContent, PortfolioAuditSummaryModal } =
      await server.ssrLoadModule('/src/PortfolioAuditSummaryModal.jsx'))
  } catch (error) {
    assert.fail(`PortfolioAuditSummaryModal must be implemented: ${error.message}`)
  }
})

after(() => server?.close())

function renderContent(props) {
  return renderToStaticMarkup(
    createElement(
      MemoryRouter,
      null,
      createElement(PortfolioAuditSummaryContent, props),
    ),
  )
}

const completeSummary = {
  id: 61,
  newsPostId: 301,
  evaluationStartTradingDayNumber: 8,
  evaluationEndTradingDayNumber: 9,
  effectiveTradingDayNumber: 10,
  extraRaisedExpectationsCount: 1,
  raisedExpectationsCount: 2,
  stableCount: 3,
  lowRiskCount: 4,
  highRiskCount: 5,
  averageScore: 2.75,
  overallDirection: 'Positive',
  createdAt: '2026-07-24T08:00:00Z',
  items: [
    {
      id: 91,
      companyId: 7,
      companyName: 'Acme Systems',
      companyRatingId: 42,
      playerQuantity: 120,
      managedFundQuantity: 80,
      rating: 'RaisedExpectations',
      totalScore: 4,
      adjustedReturnPercent: 6.25,
      dividendCoverageRatio: 2.4,
      industryTrend: 'Rising',
    },
    {
      id: 92,
      companyId: 9,
      companyName: 'Legacy Motors',
      companyRatingId: 17,
      playerQuantity: 0,
      managedFundQuantity: 25,
      rating: '',
      totalScore: null,
      adjustedReturnPercent: null,
      dividendCoverageRatio: null,
      industryTrend: null,
    },
  ],
}

test('uses the shared modal shell while the immutable summary loads', () => {
  const markup = renderToStaticMarkup(
    createElement(PortfolioAuditSummaryModal, {
      summaryId: 61,
      onClose() {},
    }),
  )

  assert.match(markup, /class="modal modal-portfolio-audit"/)
  assert.match(markup, /role="dialog" aria-modal="true"/)
  assert.match(markup, /tabindex="-1"/)
  assert.match(markup, /aria-busy="true"/)
  assert.match(markup, />Loading portfolio audit summary…</)
  assert.match(markup, /<button[^>]*type="button"[^>]*>Close<\/button>/)
})

test('renders an actionable error without inventing summary evidence', () => {
  const markup = renderContent({
    summary: null,
    loading: false,
    error: 'Could not load portfolio audit summary.',
    onRetry() {},
  })

  assert.match(markup, /role="alert"/)
  assert.match(markup, />Could not load portfolio audit summary\.</)
  assert.match(markup, /<button[^>]*>Retry<\/button>/)
  assert.doesNotMatch(markup, /Status distribution/)
})

test('shows the period, all status counts, direction, and one combined ownership row per company', () => {
  const markup = renderContent({
    summary: completeSummary,
    loading: false,
    error: null,
  })

  assert.match(markup, />Day 8–9</)
  assert.match(markup, />Day 10</)
  assert.match(markup, />2\.75</)
  assert.match(markup, /↑ Positive/)
  assert.match(markup, />Status distribution</)
  assert.equal((markup.match(/data-portfolio-audit-status=/g) ?? []).length, 5)
  assert.match(markup, /Extra raised expectations/)
  assert.match(markup, /Raised expectations/)
  assert.match(markup, />Stable</)
  assert.match(markup, /Low risk/)
  assert.match(markup, /High risk/)

  assert.equal((markup.match(/data-portfolio-company-id="7"/g) ?? []).length, 1)
  assert.match(markup, /href="\/companies\/7"[^>]*>Acme Systems<\/a>/)
  assert.match(markup, />120</)
  assert.match(markup, />80</)
  assert.match(markup, />200</)
  assert.match(markup, />4</)
  assert.match(markup, /\+6\.25%/)
  assert.match(markup, /2\.40×/)
  assert.match(markup, />Rising</)

  assert.equal((markup.match(/data-portfolio-company-id="9"/g) ?? []).length, 1)
  assert.match(markup, /Rating unavailable/)
  assert.match(markup, /Evidence unavailable/)
})

test('keeps a defensively empty immutable summary honest', () => {
  const markup = renderContent({
    summary: { ...completeSummary, items: [] },
    loading: false,
    error: null,
  })

  assert.match(markup, /No held companies were included in this audit summary\./)
  assert.doesNotMatch(markup, /data-portfolio-company-id=/)
})
