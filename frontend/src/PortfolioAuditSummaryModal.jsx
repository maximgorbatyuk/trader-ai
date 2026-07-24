import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from './api'
import { formatInt } from './format'
import { Modal } from './Modal'
import { createPortfolioAuditSummaryRequestCoordinator } from './portfolioAuditSummaryRequest'
import { RatingBadge } from './RatingBadge'

const STATUS_COUNTS = [
  { rating: 'ExtraRaisedExpectations', field: 'extraRaisedExpectationsCount' },
  { rating: 'RaisedExpectations', field: 'raisedExpectationsCount' },
  { rating: 'Stable', field: 'stableCount' },
  { rating: 'LowRisk', field: 'lowRiskCount' },
  { rating: 'HighRisk', field: 'highRiskCount' },
]

const DIRECTION_PRESENTATION = {
  Positive: { glyph: '↑', label: 'Positive', tone: 'up' },
  Neutral: { glyph: '→', label: 'Neutral', tone: 'flat' },
  Negative: { glyph: '↓', label: 'Negative', tone: 'down' },
}

function formatScore(value) {
  return typeof value === 'number' ? value.toFixed(2) : '—'
}

function formatPercent(value) {
  if (typeof value !== 'number') return '—'
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  return `${sign}${Math.abs(value).toFixed(2)}%`
}

function formatRatio(value) {
  return typeof value === 'number' ? `${value.toFixed(2)}×` : '—'
}

function SummaryValue({ label, children }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{children}</dd>
    </div>
  )
}

function StatusDistribution({ summary }) {
  return (
    <section className="portfolio-audit-section" aria-labelledby="portfolio-audit-status-title">
      <h3 id="portfolio-audit-status-title">Status distribution</h3>
      <ul className="portfolio-audit-statuses">
        {STATUS_COUNTS.map(({ rating, field }) => (
          <li key={rating} data-portfolio-audit-status={rating}>
            <RatingBadge rating={rating} />
            <span className="portfolio-audit-status-count num">{formatInt(summary[field] ?? 0)}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}

function CompanySummaryTable({ items }) {
  if (items.length === 0) {
    return <p className="note">No held companies were included in this audit summary.</p>
  }

  return (
    <section className="portfolio-audit-section" aria-labelledby="portfolio-audit-companies-title">
      <h3 id="portfolio-audit-companies-title">Company financial and audit evidence</h3>
      <div
        className="tbl-wrap portfolio-audit-table-wrap"
        role="region"
        aria-label="Scrollable company financial and audit evidence"
        tabIndex={0}
      >
        <table className="tbl portfolio-audit-table">
          <thead>
            <tr>
              <th scope="col">Company</th>
              <th scope="col">Status</th>
              <th scope="col" className="ta-r">Audit score</th>
              <th scope="col" className="ta-r">Adjusted return</th>
              <th scope="col" className="ta-r">Dividend coverage</th>
              <th scope="col">Industry</th>
              <th scope="col" className="ta-r">Player shares</th>
              <th scope="col" className="ta-r">Fund shares</th>
              <th scope="col" className="ta-r">Combined shares</th>
            </tr>
          </thead>
          <tbody>
            {items.map((item) => {
              const combinedQuantity = (item.playerQuantity ?? 0) + (item.managedFundQuantity ?? 0)
              const evidenceAvailable = typeof item.totalScore === 'number'
              return (
                <tr key={item.id} data-portfolio-company-id={item.companyId}>
                  <th scope="row">
                    <Link className="cell-link" to={`/companies/${item.companyId}`}>
                      {item.companyName}
                    </Link>
                  </th>
                  <td>
                    {item.rating ? (
                      <RatingBadge rating={item.rating} />
                    ) : (
                      <span className="muted-sub">Rating unavailable</span>
                    )}
                  </td>
                  <td className="num ta-r">
                    {evidenceAvailable ? formatInt(item.totalScore) : <span className="muted-sub">Evidence unavailable</span>}
                  </td>
                  <td className="num ta-r">{formatPercent(item.adjustedReturnPercent)}</td>
                  <td className="num ta-r">{formatRatio(item.dividendCoverageRatio)}</td>
                  <td>{item.industryTrend ?? '—'}</td>
                  <td className="num ta-r">{formatInt(item.playerQuantity ?? 0)}</td>
                  <td className="num ta-r">{formatInt(item.managedFundQuantity ?? 0)}</td>
                  <td className="num ta-r">{formatInt(combinedQuantity)}</td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
    </section>
  )
}

export function PortfolioAuditSummaryContent({ summary, loading, error, onRetry }) {
  if (loading) {
    return (
      <div className="portfolio-audit-state" role="status" aria-busy="true">
        <span className="spinner" aria-hidden="true" />
        <p className="note">Loading portfolio audit summary…</p>
      </div>
    )
  }

  if (error) {
    return (
      <div className="portfolio-audit-state">
        <p className="command-error" role="alert">{error}</p>
        {onRetry ? (
          <button className="btn" type="button" onClick={onRetry}>Retry</button>
        ) : null}
      </div>
    )
  }

  if (!summary) {
    return <p className="note">Portfolio audit summary is unavailable.</p>
  }

  const direction =
    DIRECTION_PRESENTATION[summary.overallDirection] ??
    { glyph: '·', label: summary.overallDirection || 'Unavailable', tone: 'flat' }
  const items = summary.items ?? []

  return (
    <>
      <dl className="modal-stats portfolio-audit-overview">
        <SummaryValue label="Evaluation period">
          Day {formatInt(summary.evaluationStartTradingDayNumber)}–{formatInt(summary.evaluationEndTradingDayNumber)}
        </SummaryValue>
        <SummaryValue label="Effective day">
          Day {formatInt(summary.effectiveTradingDayNumber)}
        </SummaryValue>
        <SummaryValue label="Average audit score">
          <span className="num">{formatScore(summary.averageScore)}</span>
        </SummaryValue>
        <SummaryValue label="Overall direction">
          <span className={`portfolio-audit-direction tone-${direction.tone}`}>
            {direction.glyph} {direction.label}
          </span>
        </SummaryValue>
      </dl>
      <StatusDistribution summary={summary} />
      <CompanySummaryTable items={items} />
    </>
  )
}

export function PortfolioAuditSummaryModal({ summaryId, onClose }) {
  const coordinatorRef = useRef(null)
  const [state, setState] = useState({ summary: null, loading: true, error: null })

  useEffect(() => {
    const coordinator = createPortfolioAuditSummaryRequestCoordinator({
      summaryId,
      request: api.getPortfolioAuditSummary,
      onLoading() {
        setState((current) => ({ ...current, loading: true, error: null }))
      },
      onSuccess(summary) {
        setState({ summary, loading: false, error: null })
      },
      onError(error) {
        setState((current) => ({
          ...current,
          loading: false,
          error: error?.message || 'Could not load portfolio audit summary.',
        }))
      },
    })
    coordinatorRef.current = coordinator
    const initialId = setTimeout(coordinator.load, 0)
    return () => {
      clearTimeout(initialId)
      coordinator.dispose()
      if (coordinatorRef.current === coordinator) coordinatorRef.current = null
    }
  }, [summaryId])

  const retrySummary = useCallback((event) => {
    const dialog = event.currentTarget.closest('[role="dialog"]')
    coordinatorRef.current?.retry(() => dialog?.focus?.())
  }, [])

  const titleId = `portfolio-audit-summary-title-${summaryId}`
  return (
    <Modal titleId={titleId} className="modal-portfolio-audit" onClose={onClose}>
      <header className="modal-head">
        <div className="command-id">
          <span className="command-label">Immutable audit snapshot</span>
          <h2 className="command-name" id={titleId}>
            Portfolio audit summary #{formatInt(summaryId)}
          </h2>
        </div>
        <button className="btn" type="button" onClick={onClose}>Close</button>
      </header>
      <div className="modal-body portfolio-audit-body">
        <PortfolioAuditSummaryContent
          summary={state.summary}
          loading={state.loading}
          error={state.error}
          onRetry={retrySummary}
        />
      </div>
    </Modal>
  )
}
