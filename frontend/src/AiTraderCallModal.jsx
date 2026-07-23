import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from './api'
import { formatStoredJson, parseAiCallPresentation } from './aiTraderModel'
import { aiCallStatusLabel, formatInt, formatMoney, formatSignedPercent } from './format'

function formatTimestamp(value) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString()
}

// Keeps presentation derivation client-side so the provider audit payload and backend response contract remain unchanged.
export function AiTraderCallModal({ participantId, callId, onClose }) {
  const dialogRef = useRef(null)
  const closeRef = useRef(null)
  const [call, setCall] = useState(null)
  const [companyNames, setCompanyNames] = useState(() => new Map())
  const [error, setError] = useState(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let active = true
    api
      .getParticipantAiCall(participantId, callId)
      .then((data) => {
        if (active) {
          setCall(data)
          setError(null)

          const parsedDecision = parseAiCallPresentation(null, data?.decisionJson, data?.applicationResultJson)
          const investment = parsedDecision.bigInvestment ?? parsedDecision.bigInvestmentApplication
          const companyIds = [...new Set([
            ...parsedDecision.orders.map((order) => order.companyId),
            ...(investment ? [investment.companyId] : []),
          ])]
          Promise.all(companyIds.map(async (companyId) => {
            try {
              const company = await api.getCompany(companyId)
              return [companyId, company?.name ?? null]
            } catch {
              return [companyId, null]
            }
          })).then((entries) => {
            if (active) {
              setCompanyNames(new Map(entries.filter(([, name]) => name)))
            }
          })
        }
      })
      .catch((loadError) => {
        if (active) setError(loadError.message)
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [participantId, callId])

  useEffect(() => {
    function onKeyDown(event) {
      if (event.key === 'Escape') onClose()
    }

    document.addEventListener('keydown', onKeyDown)
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.removeEventListener('keydown', onKeyDown)
      document.body.style.overflow = previousOverflow
    }
  }, [onClose])

  useEffect(() => {
    const previouslyFocused = document.activeElement
    closeRef.current?.focus()
    return () => {
      if (previouslyFocused instanceof HTMLElement) previouslyFocused.focus()
    }
  }, [])

  function onBackdropClick(event) {
    if (event.target === event.currentTarget) {
      onClose()
    }
  }

  function onDialogKeyDown(event) {
    if (event.key !== 'Tab') {
      return
    }

    const focusable = dialogRef.current?.querySelectorAll(
      'a[href], button:not([disabled]), summary, [tabindex]:not([tabindex="-1"])',
    )
    if (!focusable || focusable.length === 0) {
      return
    }

    const first = focusable[0]
    const lastFocusable = focusable[focusable.length - 1]
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault()
      lastFocusable.focus()
    } else if (!event.shiftKey && document.activeElement === lastFocusable) {
      event.preventDefault()
      first.focus()
    }
  }

  const titleId = `ai-call-modal-title-${callId}`
  const presentation = parseAiCallPresentation(
    call?.responseBody,
    call?.decisionJson,
    call?.applicationResultJson,
  )
  const investment = presentation.bigInvestment ?? presentation.bigInvestmentApplication
  const summary = presentation.summary || call?.summary || ''

  return (
    <div className="modal-backdrop" onClick={onBackdropClick}>
      <div
        className="modal modal-ai-call"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        ref={dialogRef}
        onKeyDown={onDialogKeyDown}
      >
        <header className="modal-head">
          <div className="command-id">
            <span className="command-label">AI call #{callId}</span>
            <h2 className="command-name" id={titleId}>
              {call ? `${call.providerLabel} · ${call.model}` : 'Loading…'}
            </h2>
          </div>
          <button ref={closeRef} className="btn" type="button" onClick={onClose}>
            Close
          </button>
        </header>

        <div className="modal-body">
          {loading ? <p className="note">Loading call…</p> : null}
          {error ? (
            <p className="command-error" role="alert">
              {error}
            </p>
          ) : null}
          {!loading && !error && call ? (
            <>
              <dl className="ai-call-meta">
                <div>
                  <dt>Status</dt>
                  <dd>{aiCallStatusLabel(call.status)}</dd>
                </div>
                <div>
                  <dt>Cycle</dt>
                  <dd className="num">{call.snapshotCycleNumber}</dd>
                </div>
                <div>
                  <dt>Requested</dt>
                  <dd>{formatTimestamp(call.requestedAt)}</dd>
                </div>
                <div>
                  <dt>Duration</dt>
                  <dd className="num">{call.durationMilliseconds != null ? `${call.durationMilliseconds} ms` : '—'}</dd>
                </div>
                <div>
                  <dt>Tokens</dt>
                  <dd className="num">{call.totalTokens != null ? call.totalTokens : '—'}</dd>
                </div>
                <div>
                  <dt>Applied / Rejected</dt>
                  <dd className="num">{call.appliedOrders} / {call.rejectedOrders}</dd>
                </div>
              </dl>

              {call.error ? (
                <section className="modal-section">
                  <h3 className="map-stat-label ai-call-section-title">Error</h3>
                  <p className="command-error">{call.error}</p>
                </section>
              ) : null}

              <section className="modal-section">
                <h3 className="map-stat-label ai-call-section-title">Thinking</h3>
                {presentation.thinking ? (
                  <p className="ai-call-prose">{presentation.thinking}</p>
                ) : (
                  <p className="note">Not recorded.</p>
                )}
              </section>

              <section className="modal-section">
                <h3 className="map-stat-label ai-call-section-title">Summary</h3>
                {summary ? <p className="ai-call-prose">{summary}</p> : <p className="note">Not recorded.</p>}
              </section>

              <section className="modal-section">
                <h3 className="map-stat-label ai-call-section-title" id={`ai-call-decision-${callId}`}>
                  Parsed decision
                </h3>
                {investment ? (
                  <dl className="modal-stats ai-big-investment">
                    <div>
                      <dt>Big Investment company</dt>
                      <dd>
                        <Link
                          className="cell-link"
                          to={`/companies/${investment.companyId}`}
                          onClick={onClose}
                        >
                          {companyNames.get(investment.companyId)
                            ?? `Company #${investment.companyId}`}
                        </Link>
                      </dd>
                    </div>
                    <div>
                      <dt>Amount</dt>
                      <dd className="num">{formatMoney(investment.amount)}</dd>
                    </div>
                    <div>
                      <dt>Reason</dt>
                      <dd>{investment.reason ?? '—'}</dd>
                    </div>
                    <div>
                      <dt>Outcome</dt>
                      <dd>
                        {presentation.bigInvestmentApplication
                          ? (presentation.bigInvestmentApplication.applied ? 'Applied' : 'Rejected')
                          : 'Not applied yet'}
                      </dd>
                    </div>
                    {presentation.bigInvestmentApplication?.applied ? (
                      <div>
                        <dt>Shares minted</dt>
                        <dd className="num">{formatInt(presentation.bigInvestmentApplication.sharesMinted)}</dd>
                      </div>
                    ) : null}
                    {presentation.bigInvestmentApplication?.rejectionReason ? (
                      <div>
                        <dt>Rejection reason</dt>
                        <dd>{presentation.bigInvestmentApplication.rejectionReason}</dd>
                      </div>
                    ) : null}
                  </dl>
                ) : (
                  <p className="note note-sm">No Big Investment requested.</p>
                )}
                {presentation.orders.length > 0 ? (
                  <div className="tbl-wrap">
                    <table className="tbl ai-decision-table" aria-labelledby={`ai-call-decision-${callId}`}>
                      <thead>
                        <tr>
                          <th scope="col">Side</th>
                          <th scope="col">Company</th>
                          <th scope="col" className="ta-r">Quantity</th>
                          <th scope="col" className="ta-r">Price offset</th>
                          <th scope="col">Reason</th>
                        </tr>
                      </thead>
                      <tbody>
                        {presentation.orders.map((order, index) => (
                          <tr key={`${order.companyId}-${index}`}>
                            <td><span className="tag">{order.side ?? '—'}</span></td>
                            <td>
                              <Link className="cell-link" to={`/companies/${order.companyId}`} onClick={onClose}>
                                {companyNames.get(order.companyId) ?? `Company #${order.companyId}`}
                              </Link>
                            </td>
                            <td className="num ta-r">{formatInt(order.quantity)}</td>
                            <td className="num ta-r">{formatSignedPercent(order.priceOffsetPercent)}</td>
                            <td className="ai-decision-reason">{order.reason ?? '—'}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                ) : (
                  <p className="note">No orders recorded.</p>
                )}
              </section>

              <section className="modal-section ai-call-technical">
                <h3 className="map-stat-label ai-call-section-title">Technical details</h3>
                <CallJsonDisclosure label="Raw request" value={call.requestJson} />
                <CallJsonDisclosure label="Raw response" value={call.responseBody} />
                <CallJsonDisclosure label="Parsed decision" value={call.decisionJson} />
                <CallJsonDisclosure label="Application result" value={call.applicationResultJson} />
              </section>
            </>
          ) : null}
        </div>

        <footer className="modal-foot">
          <button className="btn" type="button" onClick={onClose}>
            Close
          </button>
        </footer>
      </div>
    </div>
  )
}

function CallJsonDisclosure({ label, value }) {
  const text = formatStoredJson(value)
  return (
    <details className="ai-call-disclosure">
      <summary>
        <span>{label}</span>
        <span className="ai-call-disclosure-state">{text ? 'JSON' : 'Not recorded'}</span>
      </summary>
      {text ? <pre className="ai-call-json">{text}</pre> : <p className="note">Not recorded.</p>}
    </details>
  )
}
