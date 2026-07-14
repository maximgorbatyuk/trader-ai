import { useEffect, useRef, useState } from 'react'
import { api } from './api'
import { formatStoredJson } from './aiTraderModel'

function formatTimestamp(value) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString()
}

// Lazily loads and shows one AI call's full request, raw response, parsed decision, and application results in
// labelled scrollable text regions. Nothing is ever rendered as HTML. Contributes the dialog chrome
// (backdrop/Escape close, scroll lock, focus trap) shared by the other detail modals.
export function AiTraderCallModal({ participantId, callId, onClose }) {
  const dialogRef = useRef(null)
  const closeRef = useRef(null)
  const [call, setCall] = useState(null)
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
      'a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])',
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
                  <dd>{call.status}</dd>
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
                  <span className="map-stat-label">Error</span>
                  <p className="command-error">{call.error}</p>
                </section>
              ) : null}

              <CallJsonRegion label="Request" value={call.requestJson} />
              <CallJsonRegion label="Raw response" value={call.responseBody} />
              <CallJsonRegion label="Parsed decision" value={call.decisionJson} />
              <CallJsonRegion label="Application result" value={call.applicationResultJson} />
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

function CallJsonRegion({ label, value }) {
  const text = formatStoredJson(value)
  return (
    <section className="modal-section">
      <span className="map-stat-label">{label}</span>
      {text ? <pre className="ai-call-json">{text}</pre> : <p className="note">Not recorded.</p>}
    </section>
  )
}
