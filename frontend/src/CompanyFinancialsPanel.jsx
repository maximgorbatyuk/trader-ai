import { formatMoney, formatSigned, toneOf } from './format'
import { Panel } from './Panel'

const STABILITY_LEVEL = {
  Low: 'High',
  Medium: 'Medium',
  High: 'Low',
}

function snapshotLabel(financial) {
  const moment = {
    DayOpening: 'Day opening',
    Midday: 'Midday',
    Seed: 'Seed',
  }[financial.moment] ?? financial.moment
  return `Day ${financial.tradingDayNumber} · ${moment}`
}

function scoreValue(score, level) {
  if (typeof score !== 'number' || !level) return '—'
  return `${level} · ${score.toFixed(2)} / 100`
}

function FinancialMetric({ label, children }) {
  return (
    <div className="financial-metric">
      <dt>{label}</dt>
      <dd>{children}</dd>
    </div>
  )
}

function DirectionalMoney({ value }) {
  if (typeof value !== 'number') return '—'
  const tone = toneOf(value)
  const glyph = tone === 'up' ? '▲' : tone === 'down' ? '▼' : '◆'
  return (
    <span className={`financial-direction num tone-${tone}`}>
      <span aria-hidden="true">{glyph} </span>
      {formatSigned(value)}
    </span>
  )
}

export function CompanyFinancialsPanel({ financial }) {
  if (!financial) {
    return (
      <Panel title="Financials" count="Unavailable" className="financial-panel">
        <div className="financial-panel-body">
          <p className="note">
            <strong>Financial reporting is unavailable.</strong>{' '}
            No financial snapshot has been recorded for this company.
          </p>
        </div>
      </Panel>
    )
  }

  const latestDividend = financial.latestDividend
  const stabilityLevel = STABILITY_LEVEL[financial.financialVolatilityLevel]

  return (
    <Panel title="Financials" count={snapshotLabel(financial)} className="financial-panel">
      <div className="financial-panel-body">
        <section className="financial-section" aria-labelledby="financial-operating-title">
          <h3 id="financial-operating-title">Operating performance</h3>
          <dl className="financial-metrics">
            <FinancialMetric label="Revenue">
              <span className="num">{formatMoney(financial.revenue)}</span>
            </FinancialMetric>
            <FinancialMetric label="Net profit">
              <DirectionalMoney value={financial.netProfit} />
            </FinancialMetric>
            <FinancialMetric label="Operating cash flow">
              <DirectionalMoney value={financial.operatingCashFlow} />
            </FinancialMetric>
          </dl>
        </section>

        <section className="financial-section" aria-labelledby="financial-balance-title">
          <h3 id="financial-balance-title">Balance sheet</h3>
          <dl className="financial-metrics">
            <FinancialMetric label="Total assets">
              <span className="num">{formatMoney(financial.totalAssets)}</span>
            </FinancialMetric>
            <FinancialMetric label="Total liabilities">
              <span className="num">{formatMoney(financial.totalLiabilities)}</span>
            </FinancialMetric>
            <FinancialMetric label="Total debt">
              <span className="num">{formatMoney(financial.totalDebt)}</span>
            </FinancialMetric>
          </dl>
        </section>

        <section className="financial-section" aria-labelledby="financial-dividends-title">
          <h3 id="financial-dividends-title">Dividends</h3>
          <dl className="financial-metrics">
            <FinancialMetric label="Expected dividend per share">
              <span className="num">{formatMoney(financial.expectedDividendPerShare)} per share</span>
            </FinancialMetric>
            <FinancialMetric label="Expected dividend pool">
              <span className="num">{formatMoney(financial.expectedDividendPool)}</span>
            </FinancialMetric>
            <FinancialMetric label="Expected dividend coverage">
              <span className="num">
                {typeof financial.dividendCoverageRatio === 'number'
                  ? `${financial.dividendCoverageRatio.toFixed(2)}×`
                  : '—'}
              </span>
            </FinancialMetric>
            <FinancialMetric label="Last actual dividend outcome">
              {latestDividend?.fundingOutcome ?? 'No actual dividend recorded'}
            </FinancialMetric>
            <FinancialMetric label="Last actual dividend declared">
              <span className="num">{formatMoney(latestDividend?.declaredAmount)}</span>
            </FinancialMetric>
            <FinancialMetric label="Last actual dividend funded">
              <span className="num">{formatMoney(latestDividend?.fundedAmount)}</span>
            </FinancialMetric>
          </dl>
        </section>

        <section className="financial-section" aria-labelledby="financial-indicators-title">
          <h3 id="financial-indicators-title">Derived indicators</h3>
          <dl className="financial-metrics">
            <FinancialMetric label="Profitability">
              <span className="num">
                {scoreValue(financial.profitabilityScore, financial.profitabilityLevel)}
              </span>
            </FinancialMetric>
            <FinancialMetric label="Stability">
              <span className="num">{scoreValue(financial.stabilityScore, stabilityLevel)}</span>
            </FinancialMetric>
            <FinancialMetric label="Financial volatility">
              {financial.financialVolatilityLevel ?? '—'}
            </FinancialMetric>
            <FinancialMetric label="Closure risk">
              <span className="num">
                {scoreValue(financial.closureRiskScore, financial.closureRiskLevel)}
              </span>
            </FinancialMetric>
          </dl>
        </section>
      </div>
    </Panel>
  )
}
