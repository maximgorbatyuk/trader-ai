import assert from 'node:assert/strict'
import test, { after, before } from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'

let server
let AuditDetailContent
let AuditDetailModal

before(async () => {
  server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  try {
    ;({ AuditDetailContent, AuditDetailModal } = await server.ssrLoadModule('/src/AuditDetailModal.jsx'))
  } catch (error) {
    assert.fail(`AuditDetailModal must be implemented: ${error.message}`)
  }
})

after(() => server?.close())

function renderContent(props) {
  return renderToStaticMarkup(createElement(AuditDetailContent, props))
}

const completeAudit = {
  id: 42,
  companyId: 7,
  companyName: 'Acme Systems',
  rating: 'RaisedExpectations',
  impactPercent: null,
  auditorId: 3,
  auditorName: 'Northstar Audit',
  createdInCycleId: 520,
  createdInCycleNumber: 520,
  createdAt: '2026-07-24T05:30:00Z',
  evidenceAvailable: true,
  evaluationStartTradingDayNumber: 8,
  evaluationEndTradingDayNumber: 9,
  effectiveTradingDayNumber: 10,
  ruleVersion: 'company-audit-v1',
  notes: 'Trading days 8-9: score 14, rating RaisedExpectations.',
  totalScore: 14,
  adjustedReturnScore: 3,
  cycleJumpScore: -1,
  freeShareEmissionScore: -2,
  denominationScore: 1,
  dividendOutcomeScore: 2,
  dividendCoverageScore: 3,
  industryScore: 2,
  profitabilityFactorScore: 4,
  stabilityFactorScore: 3,
  closureRiskFactorScore: 2,
  managementOutlookFactorScore: 1,
  startPrice: 100,
  endPrice: 112.5,
  adjustedReturnPercent: 12.5,
  maximumAdjustedCycleMovePercent: 4.25,
  openingIssuedShares: 10_000,
  emittedShares: 500,
  freeShareDilutionPercent: 5,
  stockSplitCount: 1,
  reverseSplitCount: 0,
  latestDividend: {
    id: 11,
    declaredAmount: 20_000,
    fundedAmount: 18_000,
    fundingOutcome: 'Reduced',
    issuerCashBeforeFunding: 22_000,
    createdInCycleId: 490,
    tradingDayNumber: 7,
    createdAt: '2026-07-23T05:30:00Z',
  },
  issuerCash: 90_000,
  modeledMaximumDividend: 30_000,
  dividendCoverageRatio: 3,
  openingIndustrySentiment: -5,
  closingIndustrySentiment: 12,
  industryTrend: 'Rising',
  financial: {
    id: 91,
    createdInCycleId: 510,
    tradingDayNumber: 9,
    moment: 'Midday',
    createdAt: '2026-07-24T05:00:00Z',
    revenue: 250_000,
    netProfit: 32_000,
    operatingCashFlow: 28_000,
    totalAssets: 410_000,
    totalLiabilities: 150_000,
    totalDebt: 80_000,
    expectedDividendPerShare: 1.5,
    expectedDividendPool: 30_000,
    dividendCoverageRatio: 3,
    latestDividend: null,
    businessRiskScore: 24,
    businessRiskLevel: 'Low',
    managementRevenueForecast: 275_000,
    managementProfitForecast: 36_000,
    managementOperatingCashFlowForecast: 31_000,
    managementOutlook: 'Positive',
    managementConfidenceScore: 82,
    profitabilityScore: 78,
    profitabilityLevel: 'High',
    stabilityScore: 74,
    financialVolatilityLevel: 'Low',
    closureRiskScore: 18,
    closureRiskLevel: 'Low',
    changedMetrics: 'Revenue, NetProfit',
  },
  previousFinancial: {
    revenue: 200_000,
    netProfit: 0,
  },
  absoluteFinancialDelta: {
    revenue: 50_000,
    netProfit: 32_000,
  },
  percentageFinancialDelta: {
    revenue: 25,
    netProfit: null,
  },
  denominationEvents: [
    {
      id: 6,
      actionType: 'Split',
      ratio: 2,
      issuedSharesBefore: 5_000,
      issuedSharesAfter: 10_000,
      priceBefore: 200,
      priceAfter: 100,
      effectiveInCycleId: 480,
      effectiveInCycleNumber: 480,
      tradingDayNumber: 8,
      createdAt: '2026-07-22T05:30:00Z',
    },
  ],
  freeShareEmissionEvents: [
    {
      id: 9,
      sharesEmitted: 500,
      recipientCount: 25,
      createdInCycleId: 500,
      createdInCycleNumber: 500,
      tradingDayNumber: 9,
      createdAt: '2026-07-23T06:00:00Z',
    },
  ],
}

test('uses the shared modal shell for loading and focus behavior', () => {
  const markup = renderToStaticMarkup(
    createElement(AuditDetailModal, {
      companyId: 7,
      auditId: 42,
      onClose() {},
    }),
  )

  assert.match(markup, /class="modal modal-audit"/)
  assert.match(markup, /role="dialog" aria-modal="true"/)
  assert.match(markup, /tabindex="-1"/)
  assert.match(markup, /aria-busy="true"/)
  assert.match(markup, />Loading audit evidence…</)
  assert.match(markup, /<button[^>]*type="button"[^>]*>Close<\/button>/)
})

test('renders an actionable error without inventing audit evidence', () => {
  const markup = renderContent({
    audit: null,
    loading: false,
    error: 'Could not load audit evidence.',
    onRetry() {},
  })

  assert.match(markup, /role="alert"/)
  assert.match(markup, />Could not load audit evidence\.</)
  assert.match(markup, /<button[^>]*>Retry<\/button>/)
  assert.doesNotMatch(markup, /Factor scores/)
})

test('keeps legacy audits visible and labels unavailable evidence honestly', () => {
  const markup = renderContent({
    loading: false,
    error: null,
    audit: {
      ...completeAudit,
      id: 17,
      rating: 'HighRisk',
      evidenceAvailable: false,
      evaluationStartTradingDayNumber: null,
      evaluationEndTradingDayNumber: null,
      effectiveTradingDayNumber: null,
      ruleVersion: null,
      notes: null,
      totalScore: null,
      financial: null,
      denominationEvents: [],
      freeShareEmissionEvents: [],
    },
  })

  assert.match(markup, /Acme Systems/)
  assert.match(markup, /Northstar Audit/)
  assert.match(markup, /High risk/)
  assert.match(markup, /Evidence was not recorded for this legacy audit\./)
  assert.doesNotMatch(markup, /Factor scores/)
})

test('renders factor scores and every stored evidence group', () => {
  const markup = renderContent({
    audit: completeAudit,
    loading: false,
    error: null,
  })

  assert.match(markup, />Day 8–9</)
  assert.match(markup, />Day 10</)
  assert.match(markup, />Northstar Audit</)
  assert.match(markup, />Raised expectations</)
  assert.match(markup, />14</)
  assert.match(markup, />Rule version</)
  assert.match(markup, />company-audit-v1</)
  assert.match(markup, />Audit notes</)
  assert.match(markup, /Trading days 8-9: score 14, rating RaisedExpectations\./)

  assert.match(markup, />Factor scores</)
  assert.equal((markup.match(/data-audit-factor=/g) ?? []).length, 11)
  assert.match(markup, />Adjusted return</)
  assert.match(markup, />Maximum cycle jump</)
  assert.match(markup, />Free-share dilution</)
  assert.match(markup, />Profitability</)
  assert.match(markup, />Management outlook</)

  assert.match(markup, />Price evidence</)
  assert.match(markup, /\$100\.00/)
  assert.match(markup, /\$112\.50/)
  assert.match(markup, /\+12\.50%/)
  assert.match(markup, /4\.25%/)

  assert.match(markup, />Financial evidence</)
  assert.match(markup, /\$250,000\.00/)
  assert.match(markup, /\$32,000\.00/)
  assert.match(markup, /\$28,000\.00/)
  assert.match(markup, /\$410,000\.00/)
  assert.match(markup, /\$150,000\.00/)
  assert.match(markup, /\$80,000\.00/)
  assert.match(markup, /\$1\.50/)
  assert.match(markup, /\$30,000\.00/)
  assert.match(markup, /Revenue, NetProfit/)
  assert.match(markup, /Low · 24\.00 \/ 100/)
  assert.equal((markup.match(/74\.00 \/ 100 · Low volatility/g) ?? []).length, 2)
  assert.equal((markup.match(/data-financial-metric=/g) ?? []).length, 17)
  assert.match(markup, />Previous</)
  assert.match(markup, />Absolute delta</)
  assert.match(markup, />Percentage delta</)
  assert.match(
    markup,
    /data-financial-metric="revenue".*\$250,000\.00.*\$200,000\.00.*\+\$50,000\.00.*\+25\.00%/,
  )
  assert.match(
    markup,
    /data-financial-metric="netProfit".*\$32,000\.00.*\$0\.00.*\+\$32,000\.00.*—/,
  )

  assert.match(markup, />Dividend evidence</)
  assert.match(markup, /data-audit-factor="dividend-outcome".*Reduced · Day 7/)
  assert.match(markup, /Latest trading day<\/dt><dd>Day 7</)
  assert.match(markup, />Reduced</)
  assert.match(markup, /\$20,000\.00/)
  assert.match(markup, /\$18,000\.00/)
  assert.match(markup, /\$30,000\.00/)
  assert.match(markup, /3\.00×/)

  assert.match(markup, />Share emissions</)
  assert.match(markup, />500</)
  assert.match(markup, />25</)
  assert.match(markup, />Denomination events</)
  assert.match(markup, />Split 2:1</)
  assert.match(markup, />Industry evidence</)
  assert.match(markup, />Rising</)
  assert.match(markup, /−5 → \+12/)
})
