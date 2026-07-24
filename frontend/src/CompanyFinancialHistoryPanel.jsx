import { useState } from 'react'
import { formatCompactMoney, formatMoney, formatSigned, toneOf } from './format'
import { LineChart } from './LineChart'
import { Panel } from './Panel'
import { Pager } from './TableControls'

const MONEY_METRICS = [
  ['revenue', 'Revenue'],
  ['netProfit', 'Net profit'],
  ['operatingCashFlow', 'Operating cash flow'],
  ['totalAssets', 'Total assets'],
  ['totalLiabilities', 'Total liabilities', true],
  ['totalDebt', 'Total debt', true],
  ['expectedDividendPerShare', 'Expected dividend per share'],
  ['expectedDividendPool', 'Expected dividend pool'],
  ['managementRevenueForecast', 'Management revenue forecast'],
  ['managementProfitForecast', 'Management profit forecast'],
  ['managementOperatingCashFlowForecast', 'Management operating cash flow forecast'],
]

const SCORE_METRICS = [
  ['businessRiskScore', 'Business risk score', true],
  ['managementConfidenceScore', 'Management confidence score'],
  ['profitabilityScore', 'Profitability score'],
  ['stabilityScore', 'Stability score'],
  ['closureRiskScore', 'Closure risk score', true],
]

const METRICS = [
  ...MONEY_METRICS.map(([key, label, inverse = false]) => ({ key, label, unit: 'money', inverse })),
  {
    key: 'dividendCoverageRatio',
    label: 'Dividend coverage ratio',
    unit: 'ratio',
    inverse: false,
  },
  ...SCORE_METRICS.map(([key, label, inverse = false]) => ({ key, label, unit: 'score', inverse })),
]

const METRIC_BY_KEY = Object.fromEntries(METRICS.map((metric) => [metric.key, metric]))

function checkpointLabel(snapshot) {
  const moment = {
    Seed: 'Seed',
    DayOpening: 'Opening',
    Midday: 'Midday',
  }[snapshot.moment] ?? snapshot.moment
  return `Day ${snapshot.tradingDayNumber} · ${moment}`
}

function formatScalar(value, unit) {
  if (typeof value !== 'number') return '—'
  if (unit === 'money') return formatMoney(value)
  if (unit === 'ratio') return `${value.toFixed(2)}×`
  return value.toFixed(2)
}

function formatSignedScalar(value, unit) {
  if (typeof value !== 'number') return '—'
  if (unit === 'money') return formatSigned(value)
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  const suffix = unit === 'ratio' ? '×' : ''
  return `${sign}${Math.abs(value).toFixed(2)}${suffix}`
}

function formatPercentageDelta(value) {
  if (typeof value !== 'number') return '—'
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  return `${sign}${Math.abs(value).toFixed(2)}%`
}

function metricTone(metric, value) {
  return toneOf(metric.inverse && typeof value === 'number' ? -value : value)
}

function DirectionalValue({ metric, value, percentage = false }) {
  if (typeof value !== 'number') return '—'
  const tone = metricTone(metric, value)
  const glyph = tone === 'up' ? '▲' : tone === 'down' ? '▼' : '◆'
  return (
    <span className={`financial-history-delta num tone-${tone}`}>
      <span aria-hidden="true">{glyph} </span>
      {percentage ? formatPercentageDelta(value) : formatSignedScalar(value, metric.unit)}
    </span>
  )
}

function HistoryState({ error, loading }) {
  if (loading) {
    return (
      <p className="note financial-history-state" role="status" aria-busy="true">
        Loading financial history…
      </p>
    )
  }
  if (error) {
    return (
      <div className="banner financial-history-state" role="alert">
        <strong>Couldn&apos;t load financial history.</strong>
        <span>{error}</span>
      </div>
    )
  }
  return <p className="note financial-history-state">No financial snapshots have been recorded for this company.</p>
}

export function CompanyFinancialHistoryPanel({
  history,
  page,
  onPage,
  loading = false,
  error = null,
  initialMetric = 'revenue',
}) {
  const [metricKey, setMetricKey] = useState(() => (METRIC_BY_KEY[initialMetric] ? initialMetric : 'revenue'))
  const metric = METRIC_BY_KEY[metricKey]
  const items = history?.items ?? []
  const total = history?.total ?? 0
  const pageSize = history?.pageSize || 1
  const currentPage = history?.page ?? page ?? 1
  const pageCount = Math.max(1, Math.ceil(total / pageSize))
  const chartItems = items
    .filter((item) => typeof item.current?.[metric.key] === 'number')
    .slice()
    .reverse()
  const chartValues = chartItems.map((item) => item.current[metric.key])
  const chartCycles = chartItems.map((item) => item.current.createdInCycleId)
  const chartChange =
    chartValues.length > 1 ? chartValues.at(-1) - chartValues[0] : 0
  const chartTone = metricTone(metric, chartChange)
  const chartFormat = metric.unit === 'money'
    ? formatCompactMoney
    : (value) => (metric.unit === 'ratio' ? `${value.toFixed(2)}×` : value.toFixed(2))
  const chartUnit = metric.unit === 'money' ? 'USD' : metric.unit === 'ratio' ? 'Ratio' : 'Score'

  return (
    <Panel
      title="Financial history"
      count={loading && items.length === 0 ? 'Loading' : `${total} snapshot${total === 1 ? '' : 's'}`}
      className="financial-history-panel"
    >
      <div className="financial-history-toolbar">
        <label htmlFor="financial-history-metric">Metric</label>
        <select
          id="financial-history-metric"
          className="select select-sm"
          value={metric.key}
          onChange={(event) => setMetricKey(event.target.value)}
        >
          {METRICS.map((option) => (
            <option key={option.key} value={option.key}>
              {option.label}
            </option>
          ))}
        </select>
      </div>

      {items.length === 0 ? <HistoryState error={error} loading={loading} /> : (
        <div className="financial-history-body" aria-busy={loading || undefined}>
          {error ? (
            <div className="banner financial-history-warning" role="alert">
              <strong>Showing last known financial history.</strong>
              <span>{error}</span>
            </div>
          ) : null}

          <section className="financial-history-chart" aria-label={`${metric.label} trend`}>
            <LineChart
              values={chartValues}
              cycles={chartCycles}
              tone={chartTone}
              formatValue={chartFormat}
              label={`${metric.label} history`}
              xLabel="Cycle"
              yLabel={chartUnit}
            />
          </section>

          <div className="tbl-wrap financial-history-table-wrap">
            <table className="tbl financial-history-table">
              <thead>
                <tr>
                  <th scope="col">Checkpoint</th>
                  <th scope="col" className="ta-r">Current</th>
                  <th scope="col" className="ta-r">Previous</th>
                  <th scope="col" className="ta-r">Absolute delta</th>
                  <th scope="col" className="ta-r">Percentage delta</th>
                </tr>
              </thead>
              <tbody>
                {items.map((item) => {
                  const current = item.current?.[metric.key]
                  const previous = item.previous?.[metric.key]
                  const absoluteDelta = item.absoluteDelta?.[metric.key]
                  const percentageDelta = item.percentageDelta?.[metric.key]
                  const unchanged = absoluteDelta === 0
                  return (
                    <tr key={item.current.id}>
                      <th scope="row">{checkpointLabel(item.current)}</th>
                      <td className="num ta-r">{formatScalar(current, metric.unit)}</td>
                      <td className="num ta-r">{formatScalar(previous, metric.unit)}</td>
                      <td className="ta-r">
                        <DirectionalValue metric={metric} value={absoluteDelta} />
                        {unchanged ? <span className="financial-history-unchanged">Unchanged</span> : null}
                      </td>
                      <td className="ta-r">
                        <DirectionalValue metric={metric} value={percentageDelta} percentage />
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          <Pager page={currentPage} pageCount={pageCount} onPage={onPage} />
        </div>
      )}
    </Panel>
  )
}
