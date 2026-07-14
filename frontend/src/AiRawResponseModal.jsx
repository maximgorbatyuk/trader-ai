import { useEffect, useRef } from 'react'
import { formatStoredJson } from './aiTraderModel'

// Shows the unmodified provider response body from a "Who are you" test in a scrollable text region so an
// operator can inspect a failure (for example an HTTP 429 body). The value is always rendered as plain text and
// is never evaluated or treated as HTML. Carries the same dialog chrome as the other detail modals.
export function AiRawResponseModal({ statusCode, body, onClose }) {
  const dialogRef = useRef(null)
  const closeRef = useRef(null)
  const text = formatStoredJson(body)

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

  return (
    <div className="modal-backdrop" onClick={onBackdropClick}>
      <div
        className="modal modal-ai-call"
        role="dialog"
        aria-modal="true"
        aria-labelledby="ai-test-response-title"
        ref={dialogRef}
        onKeyDown={onDialogKeyDown}
      >
        <header className="modal-head">
          <div className="command-id">
            <span className="command-label">Test response</span>
            <h2 className="command-name" id="ai-test-response-title">
              {statusCode != null ? `HTTP ${statusCode}` : 'Raw response'}
            </h2>
          </div>
          <button ref={closeRef} className="btn" type="button" onClick={onClose}>
            Close
          </button>
        </header>

        <div className="modal-body">
          <section className="modal-section">
            <span className="map-stat-label">Raw response</span>
            {text ? <pre className="ai-call-json">{text}</pre> : <p className="note">No response body.</p>}
          </section>
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
