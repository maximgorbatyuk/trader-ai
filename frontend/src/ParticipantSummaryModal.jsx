import { useEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import { formatInt, formatMoney, TRADER_TYPE_LABEL } from './format'
import { TemperamentTag } from './TemperamentTag'

// Short trader summary opened from the dashboard Traders table: type, balances, and how many companies the
// trader holds, with a link into the full Traders page. Contributes only the dialog chrome (backdrop/Escape
// close, scroll lock, focus trap); the body is a small stats grid.
export function ParticipantSummaryModal({ participant, onClose }) {
  const dialogRef = useRef(null)
  const closeRef = useRef(null)

  // Close on Escape and lock background scroll while the dialog is open.
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

  // Move focus into the dialog on open and restore it to the trigger on close.
  useEffect(() => {
    const previouslyFocused = document.activeElement
    closeRef.current?.focus()
    return () => {
      if (previouslyFocused instanceof HTMLElement) previouslyFocused.focus()
    }
  }, [])

  if (!participant) {
    return null
  }

  const totalWorth = (participant.currentBalance ?? 0) + (participant.holdingsValue ?? 0)
  const titleId = `trader-modal-title-${participant.id}`

  function onBackdropClick(event) {
    if (event.target === event.currentTarget) {
      onClose()
    }
  }

  // Keep Tab focus inside the dialog by wrapping it at the first and last focusable controls.
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
        className="modal modal-compact"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        ref={dialogRef}
        onKeyDown={onDialogKeyDown}
      >
        <header className="modal-head">
          <div className="command-id">
            <span className="command-label">Trader</span>
            <div className="command-name-row">
              <h2 className="command-name" id={titleId}>
                {participant.name}
              </h2>
              <TemperamentTag temperament={participant.temperament} type={participant.type} />
            </div>
          </div>
        </header>

        <div className="modal-body">
          <dl className="modal-stats">
            <div>
              <dt>Type</dt>
              <dd>{TRADER_TYPE_LABEL[participant.type] ?? participant.type}</dd>
            </div>
            <div>
              <dt>Current balance</dt>
              <dd className="num">{formatMoney(participant.currentBalance)}</dd>
            </div>
            <div>
              <dt>Total worth</dt>
              <dd className="num">{formatMoney(totalWorth)}</dd>
            </div>
            <div>
              <dt>Companies owned</dt>
              <dd className="num">{formatInt(participant.companiesOwned ?? 0)}</dd>
            </div>
          </dl>
        </div>

        <footer className="modal-foot">
          <button type="button" className="btn" ref={closeRef} onClick={onClose}>
            Close
          </button>
          <Link className="btn btn-primary" to={`/traders?trader=${participant.id}`} onClick={onClose}>
            Open in Traders page
          </Link>
        </footer>
      </div>
    </div>
  )
}
