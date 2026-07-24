import assert from 'node:assert/strict'
import { after, before, test } from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'

let server
let CompanyManagementOutlookPanel

before(async () => {
  server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  const companyDetailModule = await server.ssrLoadModule('/src/CompanyDetail.jsx')
  CompanyManagementOutlookPanel = companyDetailModule.CompanyManagementOutlookPanel
})

after(() => server?.close())

test('shows forecasts, outlook, confidence, and a textual business-risk level', () => {
  assert.equal(typeof CompanyManagementOutlookPanel, 'function')

  const financial = {
    tradingDayNumber: 4,
    moment: 'DayOpening',
    managementRevenueForecast: 136_000,
    managementProfitForecast: -13_500,
    managementOperatingCashFlowForecast: 10_200,
    managementOutlook: 'Positive',
    managementConfidenceScore: 76.4,
    businessRiskScore: 28.5,
    businessRiskLevel: 'High',
  }

  const markup = renderToStaticMarkup(createElement(CompanyManagementOutlookPanel, { financial }))

  for (const label of [
    'Revenue forecast',
    'Profit forecast',
    'Operating cash flow forecast',
    'Management outlook',
    'Management confidence',
    'Business risk',
  ]) {
    assert.match(markup, new RegExp(`>${label}<`))
  }

  assert.match(markup, /Day 4 · Day opening/)
  assert.match(markup, /▼.*−\$13,500\.00/)
  assert.match(markup, /▲.*Positive/)
  assert.match(markup, />76\.40 \/ 100</)
  assert.match(markup, />High · 28\.50 \/ 100</)
})

test('uses an honest unavailable state instead of inventing management guidance', () => {
  assert.equal(typeof CompanyManagementOutlookPanel, 'function')

  const markup = renderToStaticMarkup(createElement(CompanyManagementOutlookPanel, { financial: null }))

  assert.match(markup, /Management guidance is unavailable/)
  assert.match(markup, /No financial snapshot has been recorded for this company\./)
  assert.doesNotMatch(markup, /Neutral/)
})
