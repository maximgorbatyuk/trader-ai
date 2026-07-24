import { formatMoney, formatSigned, toneOf } from './format'
import { Panel } from './Panel'

function snapshotLabel(financial) {
  const moment = {
    DayOpening: 'Day opening',
    Midday: 'Midday',
    Seed: 'Seed',
  }[financial.moment] ?? financial.moment
  return `Day ${financial.tradingDayNumber} · ${moment}`
}

function businessRiskLevel(score) {
  if (typeof score !== 'number') return null
  if (score <= 34) return 'Low'
  return score >= 67 ? 'High' : 'Medium'
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

function Outlook({ value }) {
  const presentation = {
    Positive: { glyph: '▲', tone: 'up' },
    Negative: { glyph: '▼', tone: 'down' },
    Neutral: { glyph: '◆', tone: 'flat' },
  }[value]
  if (!presentation) return '—'
  return (
    <span className={`financial-direction tone-${presentation.tone}`}>
      <span aria-hidden="true">{presentation.glyph} </span>
      {value}
    </span>
  )
}

export function CompanyManagementOutlookPanel({ financial }) {
  if (!financial) {
    return (
      <Panel title="Management outlook" count="Unavailable" className="financial-panel">
        <div className="financial-panel-body">
          <p className="note">
            <strong>Management guidance is unavailable.</strong>{' '}
            No financial snapshot has been recorded for this company.
          </p>
        </div>
      </Panel>
    )
  }

  const riskLevel = businessRiskLevel(financial.businessRiskScore)

  return (
    <Panel title="Management outlook" count={snapshotLabel(financial)} className="financial-panel">
      <div className="financial-panel-body">
        <section className="financial-section" aria-labelledby="management-forecast-title">
          <h3 id="management-forecast-title">Forecasts</h3>
          <dl className="financial-metrics">
            <FinancialMetric label="Revenue forecast">
              <span className="num">{formatMoney(financial.managementRevenueForecast)}</span>
            </FinancialMetric>
            <FinancialMetric label="Profit forecast">
              <DirectionalMoney value={financial.managementProfitForecast} />
            </FinancialMetric>
            <FinancialMetric label="Operating cash flow forecast">
              <DirectionalMoney value={financial.managementOperatingCashFlowForecast} />
            </FinancialMetric>
          </dl>
        </section>

        <section className="financial-section" aria-labelledby="management-assessment-title">
          <h3 id="management-assessment-title">Management assessment</h3>
          <dl className="financial-metrics">
            <FinancialMetric label="Management outlook">
              <Outlook value={financial.managementOutlook} />
            </FinancialMetric>
            <FinancialMetric label="Management confidence">
              <span className="num">
                {typeof financial.managementConfidenceScore === 'number'
                  ? `${financial.managementConfidenceScore.toFixed(2)} / 100`
                  : '—'}
              </span>
            </FinancialMetric>
            <FinancialMetric label="Business risk">
              <span className="num">
                {typeof financial.businessRiskScore === 'number' && riskLevel
                  ? `${riskLevel} · ${financial.businessRiskScore.toFixed(2)} / 100`
                  : '—'}
              </span>
            </FinancialMetric>
          </dl>
        </section>
      </div>
    </Panel>
  )
}
