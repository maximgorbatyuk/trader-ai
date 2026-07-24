import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from './api'
import { formatInt, formatMoney } from './format'
import { Modal } from './Modal'
import { RatingBadge } from './RatingBadge'

function formatPercent(value, { signed = false } = {}) {
  if (typeof value !== 'number') return '—'
  const sign = signed ? (value > 0 ? '+' : value < 0 ? '−' : '') : ''
  return `${sign}${Math.abs(value).toFixed(2)}%`
}

function formatRatio(value) {
  return typeof value === 'number' ? `${value.toFixed(2)}×` : '—'
}

function scoreAndLevel(score, level) {
  if (typeof score !== 'number') return level ?? '—'
  return level ? `${level} · ${score.toFixed(2)} / 100` : `${score.toFixed(2)} / 100`
}

function stabilityAndVolatility(score, volatilityLevel) {
  if (typeof score !== 'number') {
    return volatilityLevel ? `${volatilityLevel} volatility` : '—'
  }
  return volatilityLevel
    ? `${score.toFixed(2)} / 100 · ${volatilityLevel} volatility`
    : `${score.toFixed(2)} / 100`
}

function EvidenceValue({ label, children }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{children}</dd>
    </div>
  )
}

function FactorScores({ audit }) {
  const financial = audit.financial
  const rows = [
    {
      key: 'adjusted-return',
      label: 'Adjusted return',
      observed: formatPercent(audit.adjustedReturnPercent, { signed: true }),
      score: audit.adjustedReturnScore,
      explanation: 'Return over the audit period after share-denomination adjustments.',
    },
    {
      key: 'cycle-jump',
      label: 'Maximum cycle jump',
      observed: formatPercent(audit.maximumAdjustedCycleMovePercent),
      score: audit.cycleJumpScore,
      explanation: 'Largest absolute adjusted price movement inside one market cycle.',
    },
    {
      key: 'emission',
      label: 'Free-share dilution',
      observed: formatPercent(audit.freeShareDilutionPercent),
      score: audit.freeShareEmissionScore,
      explanation: 'New free shares relative to the issued supply at the start of the period.',
    },
    {
      key: 'denomination',
      label: 'Denomination actions',
      observed: `${formatInt(audit.stockSplitCount)} split · ${formatInt(audit.reverseSplitCount)} reverse`,
      score: audit.denominationScore,
      explanation: 'Stock splits and reverse splits completed during the audit period.',
    },
    {
      key: 'dividend-outcome',
      label: 'Dividend outcome',
      observed: audit.latestDividend?.fundingOutcome ?? 'No actual dividend recorded',
      score: audit.dividendOutcomeScore,
      explanation: 'Whether the latest declared dividend was paid, reduced, or skipped.',
    },
    {
      key: 'dividend-coverage',
      label: 'Expected dividend coverage',
      observed: formatRatio(audit.dividendCoverageRatio),
      score: audit.dividendCoverageScore,
      explanation: 'Issuer cash capacity relative to the modeled next dividend.',
    },
    {
      key: 'industry',
      label: 'Industry direction',
      observed: audit.industryTrend ?? 'Unavailable',
      score: audit.industryScore,
      explanation: 'Industry sentiment movement across the audit period.',
    },
    {
      key: 'profitability',
      label: 'Profitability',
      observed: scoreAndLevel(financial?.profitabilityScore, financial?.profitabilityLevel),
      score: audit.profitabilityFactorScore,
      explanation: 'Profit generation measured from the financial snapshot used by the auditor.',
    },
    {
      key: 'stability',
      label: 'Financial stability',
      observed: stabilityAndVolatility(financial?.stabilityScore, financial?.financialVolatilityLevel),
      score: audit.stabilityFactorScore,
      explanation: 'Stability score together with the observed financial-volatility level.',
    },
    {
      key: 'closure-risk',
      label: 'Closure risk',
      observed: scoreAndLevel(financial?.closureRiskScore, financial?.closureRiskLevel),
      score: audit.closureRiskFactorScore,
      explanation: 'Modeled risk that the company may be unable to continue operating.',
    },
    {
      key: 'management',
      label: 'Management outlook',
      observed: financial
        ? `${financial.managementOutlook} · ${financial.managementConfidenceScore.toFixed(2)} / 100 confidence`
        : 'Unavailable',
      score: audit.managementOutlookFactorScore,
      explanation: 'Management forecast direction weighted by its recorded confidence.',
    },
  ]

  return (
    <section className="audit-modal-section" aria-labelledby="audit-factor-title">
      <h3 id="audit-factor-title">Factor scores</h3>
      <div className="tbl-wrap">
        <table className="tbl audit-factor-table">
          <thead>
            <tr>
              <th scope="col">Factor</th>
              <th scope="col">Observed</th>
              <th scope="col" className="ta-r">Score</th>
              <th scope="col">Interpretation</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.key} data-audit-factor={row.key}>
                <th scope="row">{row.label}</th>
                <td className="num">{row.observed}</td>
                <td className="num ta-r">{formatInt(row.score)}</td>
                <td>{row.explanation}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function PriceEvidence({ audit }) {
  return (
    <section className="audit-modal-section" aria-labelledby="audit-price-title">
      <h3 id="audit-price-title">Price evidence</h3>
      <dl className="modal-stats">
        <EvidenceValue label="Period start">
          <span className="num">{formatMoney(audit.startPrice)}</span>
        </EvidenceValue>
        <EvidenceValue label="Period end">
          <span className="num">{formatMoney(audit.endPrice)}</span>
        </EvidenceValue>
        <EvidenceValue label="Adjusted return">
          <span className="num">{formatPercent(audit.adjustedReturnPercent, { signed: true })}</span>
        </EvidenceValue>
        <EvidenceValue label="Maximum cycle jump">
          <span className="num">{formatPercent(audit.maximumAdjustedCycleMovePercent)}</span>
        </EvidenceValue>
      </dl>
    </section>
  )
}

function FinancialEvidence({ financial }) {
  return (
    <section className="audit-modal-section" aria-labelledby="audit-financial-title">
      <h3 id="audit-financial-title">Financial evidence</h3>
      {financial ? (
        <dl className="modal-stats audit-evidence-grid">
          <EvidenceValue label="Snapshot">
            Day {formatInt(financial.tradingDayNumber)} · {financial.moment}
          </EvidenceValue>
          <EvidenceValue label="Revenue">
            <span className="num">{formatMoney(financial.revenue)}</span>
          </EvidenceValue>
          <EvidenceValue label="Net profit">
            <span className="num">{formatMoney(financial.netProfit)}</span>
          </EvidenceValue>
          <EvidenceValue label="Operating cash flow">
            <span className="num">{formatMoney(financial.operatingCashFlow)}</span>
          </EvidenceValue>
          <EvidenceValue label="Total assets">
            <span className="num">{formatMoney(financial.totalAssets)}</span>
          </EvidenceValue>
          <EvidenceValue label="Total liabilities">
            <span className="num">{formatMoney(financial.totalLiabilities)}</span>
          </EvidenceValue>
          <EvidenceValue label="Total debt">
            <span className="num">{formatMoney(financial.totalDebt)}</span>
          </EvidenceValue>
          <EvidenceValue label="Expected dividend per share">
            <span className="num">{formatMoney(financial.expectedDividendPerShare)}</span>
          </EvidenceValue>
          <EvidenceValue label="Expected dividend pool">
            <span className="num">{formatMoney(financial.expectedDividendPool)}</span>
          </EvidenceValue>
          <EvidenceValue label="Snapshot dividend coverage">
            <span className="num">{formatRatio(financial.dividendCoverageRatio)}</span>
          </EvidenceValue>
          <EvidenceValue label="Business risk">
            <span className="num">
              {scoreAndLevel(financial.businessRiskScore, financial.businessRiskLevel)}
            </span>
          </EvidenceValue>
          <EvidenceValue label="Profitability">
            <span className="num">
              {scoreAndLevel(financial.profitabilityScore, financial.profitabilityLevel)}
            </span>
          </EvidenceValue>
          <EvidenceValue label="Stability">
            <span className="num">
              {stabilityAndVolatility(financial.stabilityScore, financial.financialVolatilityLevel)}
            </span>
          </EvidenceValue>
          <EvidenceValue label="Closure risk">
            <span className="num">
              {scoreAndLevel(financial.closureRiskScore, financial.closureRiskLevel)}
            </span>
          </EvidenceValue>
          <EvidenceValue label="Management guidance">
            {financial.managementOutlook} ·{' '}
            <span className="num">{financial.managementConfidenceScore.toFixed(2)} / 100</span>
          </EvidenceValue>
          <EvidenceValue label="Revenue forecast">
            <span className="num">{formatMoney(financial.managementRevenueForecast)}</span>
          </EvidenceValue>
          <EvidenceValue label="Profit forecast">
            <span className="num">{formatMoney(financial.managementProfitForecast)}</span>
          </EvidenceValue>
          <EvidenceValue label="Cash-flow forecast">
            <span className="num">{formatMoney(financial.managementOperatingCashFlowForecast)}</span>
          </EvidenceValue>
          <EvidenceValue label="Changed metrics">
            {financial.changedMetrics || 'None recorded'}
          </EvidenceValue>
        </dl>
      ) : (
        <p className="note note-sm">No financial snapshot was available to this audit.</p>
      )}
    </section>
  )
}

function DividendEvidence({ audit }) {
  const dividend = audit.latestDividend
  return (
    <section className="audit-modal-section" aria-labelledby="audit-dividend-title">
      <h3 id="audit-dividend-title">Dividend evidence</h3>
      <dl className="modal-stats audit-evidence-grid">
        <EvidenceValue label="Latest outcome">
          {dividend?.fundingOutcome ?? 'No actual dividend recorded'}
        </EvidenceValue>
        <EvidenceValue label="Declared">
          <span className="num">{formatMoney(dividend?.declaredAmount)}</span>
        </EvidenceValue>
        <EvidenceValue label="Funded">
          <span className="num">{formatMoney(dividend?.fundedAmount)}</span>
        </EvidenceValue>
        <EvidenceValue label="Issuer cash before funding">
          <span className="num">{formatMoney(dividend?.issuerCashBeforeFunding)}</span>
        </EvidenceValue>
        <EvidenceValue label="Issuer cash at audit">
          <span className="num">{formatMoney(audit.issuerCash)}</span>
        </EvidenceValue>
        <EvidenceValue label="Modeled next dividend">
          <span className="num">{formatMoney(audit.modeledMaximumDividend)}</span>
        </EvidenceValue>
        <EvidenceValue label="Expected coverage">
          <span className="num">{formatRatio(audit.dividendCoverageRatio)}</span>
        </EvidenceValue>
      </dl>
    </section>
  )
}

function EmissionEvidence({ audit }) {
  const events = audit.freeShareEmissionEvents ?? []
  return (
    <section className="audit-modal-section" aria-labelledby="audit-emission-title">
      <h3 id="audit-emission-title">Share emissions</h3>
      <dl className="modal-stats">
        <EvidenceValue label="Opening issued shares">
          <span className="num">{formatInt(audit.openingIssuedShares)}</span>
        </EvidenceValue>
        <EvidenceValue label="Free shares emitted">
          <span className="num">{formatInt(audit.emittedShares)}</span>
        </EvidenceValue>
        <EvidenceValue label="Dilution">
          <span className="num">{formatPercent(audit.freeShareDilutionPercent)}</span>
        </EvidenceValue>
      </dl>
      {events.length > 0 ? (
        <div className="tbl-wrap">
          <table className="tbl audit-event-table">
            <thead>
              <tr>
                <th scope="col">Trading day</th>
                <th scope="col" className="ta-r">Shares</th>
                <th scope="col" className="ta-r">Recipients</th>
                <th scope="col" className="ta-r">Cycle</th>
              </tr>
            </thead>
            <tbody>
              {events.map((event) => (
                <tr key={event.id}>
                  <th scope="row">Day {formatInt(event.tradingDayNumber)}</th>
                  <td className="num ta-r">{formatInt(event.sharesEmitted)}</td>
                  <td className="num ta-r">{formatInt(event.recipientCount)}</td>
                  <td className="num ta-r">{formatInt(event.createdInCycleNumber)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <p className="note note-sm">No free-share emission occurred during this audit period.</p>
      )}
    </section>
  )
}

function DenominationEvidence({ audit }) {
  const events = audit.denominationEvents ?? []
  return (
    <section className="audit-modal-section" aria-labelledby="audit-denomination-title">
      <h3 id="audit-denomination-title">Denomination events</h3>
      {events.length > 0 ? (
        <div className="tbl-wrap">
          <table className="tbl audit-event-table">
            <thead>
              <tr>
                <th scope="col">Action</th>
                <th scope="col">Trading day</th>
                <th scope="col" className="ta-r">Issued shares</th>
                <th scope="col" className="ta-r">Share price</th>
              </tr>
            </thead>
            <tbody>
              {events.map((event) => (
                <tr key={event.id}>
                  <th scope="row">{event.actionType} {formatInt(event.ratio)}:1</th>
                  <td>Day {formatInt(event.tradingDayNumber)}</td>
                  <td className="num ta-r">
                    {formatInt(event.issuedSharesBefore)} → {formatInt(event.issuedSharesAfter)}
                  </td>
                  <td className="num ta-r">
                    {formatMoney(event.priceBefore)} → {formatMoney(event.priceAfter)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <p className="note note-sm">No split or reverse split occurred during this audit period.</p>
      )}
    </section>
  )
}

function IndustryEvidence({ audit }) {
  const opening = typeof audit.openingIndustrySentiment === 'number'
    ? `${audit.openingIndustrySentiment >= 0 ? '+' : '−'}${Math.abs(audit.openingIndustrySentiment)}`
    : '—'
  const closing = typeof audit.closingIndustrySentiment === 'number'
    ? `${audit.closingIndustrySentiment >= 0 ? '+' : '−'}${Math.abs(audit.closingIndustrySentiment)}`
    : '—'
  return (
    <section className="audit-modal-section" aria-labelledby="audit-industry-title">
      <h3 id="audit-industry-title">Industry evidence</h3>
      <dl className="modal-stats">
        <EvidenceValue label="Direction">{audit.industryTrend ?? 'Unavailable'}</EvidenceValue>
        <EvidenceValue label="Sentiment">
          <span className="num">{opening} → {closing}</span>
        </EvidenceValue>
      </dl>
    </section>
  )
}

export function AuditDetailContent({ audit, loading, error, onRetry }) {
  if (loading) {
    return (
      <div className="audit-modal-state" role="status" aria-busy="true">
        <span className="spinner" aria-hidden="true" />
        <p className="note">Loading audit evidence…</p>
      </div>
    )
  }

  if (error) {
    return (
      <div className="audit-modal-state">
        <p className="command-error" role="alert">{error}</p>
        {onRetry ? (
          <button className="btn" type="button" onClick={onRetry}>Retry</button>
        ) : null}
      </div>
    )
  }

  if (!audit) {
    return <p className="note">Audit details are unavailable.</p>
  }

  return (
    <>
      <dl className="modal-stats audit-modal-meta">
        <EvidenceValue label="Company">{audit.companyName}</EvidenceValue>
        <EvidenceValue label="Auditor">{audit.auditorName}</EvidenceValue>
        <EvidenceValue label="Status">
          <RatingBadge rating={audit.rating} impactPercent={audit.impactPercent} />
        </EvidenceValue>
        <EvidenceValue label="Recorded">
          Cycle <span className="num">{formatInt(audit.createdInCycleNumber)}</span>
        </EvidenceValue>
        <EvidenceValue label="Evaluation period">
          {audit.evidenceAvailable
            ? `Day ${formatInt(audit.evaluationStartTradingDayNumber)}–${formatInt(audit.evaluationEndTradingDayNumber)}`
            : 'Unavailable'}
        </EvidenceValue>
        <EvidenceValue label="Effective">
          {audit.evidenceAvailable ? `Day ${formatInt(audit.effectiveTradingDayNumber)}` : 'Unavailable'}
        </EvidenceValue>
        <EvidenceValue label="Total score">
          <span className="num">{formatInt(audit.totalScore)}</span>
        </EvidenceValue>
      </dl>

      {!audit.evidenceAvailable ? (
        <p className="note audit-legacy-note">
          <strong>Evidence was not recorded for this legacy audit.</strong>{' '}
          The verdict metadata is retained, but its historical factors cannot be reconstructed.
        </p>
      ) : (
        <>
          <FactorScores audit={audit} />
          <PriceEvidence audit={audit} />
          <FinancialEvidence financial={audit.financial} />
          <DividendEvidence audit={audit} />
          <EmissionEvidence audit={audit} />
          <DenominationEvidence audit={audit} />
          <IndustryEvidence audit={audit} />
        </>
      )}
    </>
  )
}

export function AuditDetailModal({ companyId, auditId, onClose }) {
  const requestId = useRef(0)
  const [state, setState] = useState({ audit: null, loading: true, error: null })

  const loadAudit = useCallback(() => {
    const currentRequest = requestId.current + 1
    requestId.current = currentRequest
    setState((current) => ({ ...current, loading: true, error: null }))
    api
      .getCompanyAudit(companyId, auditId)
      .then((audit) => {
        if (requestId.current === currentRequest) {
          setState({ audit, loading: false, error: null })
        }
      })
      .catch((error) => {
        if (requestId.current === currentRequest) {
          setState((current) => ({
            ...current,
            loading: false,
            error: error.message ?? 'Could not load audit evidence.',
          }))
        }
      })
  }, [auditId, companyId])

  useEffect(() => {
    const initialId = setTimeout(loadAudit, 0)
    return () => {
      clearTimeout(initialId)
      requestId.current += 1
    }
  }, [loadAudit])

  const titleId = `audit-modal-title-${auditId}`
  return (
    <Modal titleId={titleId} className="modal-audit" onClose={onClose}>
      <header className="modal-head">
        <div className="command-id">
          <span className="command-label">Audit #{formatInt(auditId)}</span>
          <h2 className="command-name" id={titleId}>
            {state.audit?.companyName ?? 'Audit evidence'}
          </h2>
        </div>
        <button className="btn" type="button" onClick={onClose}>Close</button>
      </header>
      <div className="modal-body audit-modal-body">
        <AuditDetailContent
          audit={state.audit}
          loading={state.loading}
          error={state.error}
          onRetry={loadAudit}
        />
      </div>
    </Modal>
  )
}
