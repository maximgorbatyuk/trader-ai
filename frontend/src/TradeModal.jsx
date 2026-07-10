import { useEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import { formatInt, formatMoney } from './format'

// A null seller is the share issuer's own offering.
function party(id, name) {
  if (id == null) return { label: 'Issuer', to: null }
  return { label: name ?? `#${id}`, to: `/traders/${id}` }
}

function formatPct(fraction) {
  if (typeof fraction !== 'number') return '—'
  const sign = fraction > 0 ? '+' : fraction < 0 ? '−' : ''
  return `${sign}${(Math.abs(fraction) * 100).toFixed(2)}%`
}

function priceVsMarket(price, reference) {
  if (typeof price !== 'number' || typeof reference !== 'number' || reference === 0) return null
  return formatPct((price - reference) / reference)
}

function formatTimestamp(value) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString()
}

// Full detail dialog for one settled trade, opened by clicking a row in a "Recent trades" table. Contributes
// the dialog chrome (backdrop/Escape close, scroll lock, focus trap) around a stats grid; company name is
// passed in because the share-transaction payload carries only the company id. When participantId is set the
// header frames the trade from that trader's side (bought vs sold).
export function TradeModal({ trade, companyName, participantId, onClose }) {
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

  if (!trade) {
    return null
  }

  const seller = party(trade.sellerId, trade.sellerName)
  const buyer = party(trade.buyerId, trade.buyerName)
  const vsMarket = priceVsMarket(trade.price, trade.marketPriceBefore)
  const bought = participantId != null ? trade.buyerId === participantId : null
  const titleId = `trade-modal-title-${trade.id}`

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
            <span className="command-label">Trade #{trade.id}</span>
            <h2 className="command-name" id={titleId}>
              {companyName ?? `#${trade.companyId}`}
            </h2>
          </div>
          {bought != null ? (
            <strong className={`num tone-${bought ? 'up' : 'down'}`}>{bought ? 'You bought' : 'You sold'}</strong>
          ) : null}
        </header>

        <div className="modal-body">
          <div className="modal-section">
            <span className="map-stat-label">Counterparties</span>
            <p className="modal-deal">
              {seller.to ? (
                <Link className="cell-link cell-ellipsis" to={seller.to} onClick={onClose}>
                  {seller.label}
                </Link>
              ) : (
                <span className="cell-ellipsis muted-sub">{seller.label}</span>
              )}
              <span className="flow-arrow" aria-label="to">
                →
              </span>
              {buyer.to ? (
                <Link className="cell-link cell-ellipsis" to={buyer.to} onClick={onClose}>
                  {buyer.label}
                </Link>
              ) : (
                <span className="cell-ellipsis muted-sub">{buyer.label}</span>
              )}
            </p>
          </div>

          <dl className="modal-stats">
            <div>
              <dt>Quantity</dt>
              <dd className="num">{formatInt(trade.quantity)}</dd>
            </div>
            <div>
              <dt>Price</dt>
              <dd className="num">
                {formatMoney(trade.price)}
                {vsMarket ? <span className="muted-sub"> {vsMarket}</span> : null}
              </dd>
            </div>
            <div>
              <dt>Market price before</dt>
              <dd className="num">{trade.marketPriceBefore != null ? formatMoney(trade.marketPriceBefore) : '—'}</dd>
            </div>
            <div>
              <dt>Total cost</dt>
              <dd className="num">{formatMoney(trade.totalCost)}</dd>
            </div>
            <div>
              <dt>Cycle</dt>
              <dd className="num">#{trade.createdInCycleId}</dd>
            </div>
            <div>
              <dt>Executed</dt>
              <dd>{formatTimestamp(trade.createdAt)}</dd>
            </div>
          </dl>
        </div>

        <footer className="modal-foot">
          <button type="button" className="btn" ref={closeRef} onClick={onClose}>
            Close
          </button>
          <Link className="btn btn-primary" to={`/companies/${trade.companyId}`} onClick={onClose}>
            Open company page
          </Link>
        </footer>
      </div>
    </div>
  )
}
