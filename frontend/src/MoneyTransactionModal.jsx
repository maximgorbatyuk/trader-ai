import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { formatInt, formatMoney } from './format'
import { CASH_LABEL, CASH_TONE } from './cashMovements'
import { api } from './api'

function formatTimestamp(value) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString()
}

// One paying company inside a dividend breakdown; links to the company page like the trade rows do.
function DividendBreakdown({ lines }) {
  return (
    <div className="modal-section">
      <span className="map-stat-label">Paid by</span>
      {lines.length === 0 ? (
        <p className="note note-sm">Per-company breakdown was not recorded for this dividend.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Company</th>
                <th scope="col" className="ta-r">
                  Amount
                </th>
              </tr>
            </thead>
            <tbody>
              {lines.map((line) => (
                <tr key={line.companyId}>
                  <th scope="row" className="cell-ellipsis">
                    <Link className="cell-link" to={`/companies/${line.companyId}`}>
                      {line.companyName ?? `#${line.companyId}`}
                    </Link>
                  </th>
                  <td className="num ta-r">{formatMoney(line.amount)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

// Detail for the trade behind a Reserve/Release/Debit/Credit/Trade fee row: which company, side, and size.
function TradeDetail({ order, trade }) {
  const companyId = trade?.companyId ?? order?.companyId
  const companyName = trade?.companyName ?? order?.companyName
  return (
    <div className="modal-section">
      <span className="map-stat-label">Related trade</span>
      <dl className="modal-stats">
        <div>
          <dt>Company</dt>
          <dd className="cell-ellipsis">
            {companyId != null ? (
              <Link className="cell-link" to={`/companies/${companyId}`}>
                {companyName ?? `#${companyId}`}
              </Link>
            ) : (
              '—'
            )}
          </dd>
        </div>
        {order ? (
          <div>
            <dt>Order</dt>
            <dd>
              {order.side} · {order.status}
            </dd>
          </div>
        ) : null}
        {trade ? (
          <>
            <div>
              <dt>Quantity</dt>
              <dd className="num">{formatInt(trade.quantity)}</dd>
            </div>
            <div>
              <dt>Price</dt>
              <dd className="num">{formatMoney(trade.price)}</dd>
            </div>
            <div>
              <dt>Total</dt>
              <dd className="num">{formatMoney(trade.totalCost)}</dd>
            </div>
          </>
        ) : (
          order && (
            <div>
              <dt>Limit price</dt>
              <dd className="num">{formatMoney(order.limitPrice)}</dd>
            </div>
          )
        )}
      </dl>
    </div>
  )
}

// Detail for a loan-driven row (disbursement, interest, repayment, fine): the loan it settled against.
function LoanDetail({ loan }) {
  return (
    <div className="modal-section">
      <span className="map-stat-label">Related loan</span>
      <dl className="modal-stats">
        <div>
          <dt>Principal</dt>
          <dd className="num">{formatMoney(loan.principal)}</dd>
        </div>
        <div>
          <dt>Remaining</dt>
          <dd className="num">{formatMoney(loan.remainingPrincipal)}</dd>
        </div>
        <div>
          <dt>Interest/cyc</dt>
          <dd className="num">{(loan.interestRatePerCycle * 100).toFixed(3)}%</dd>
        </div>
        <div>
          <dt>Principal due</dt>
          <dd className={`num${loan.pastDuePrincipal > 0 ? ' tone-attention' : ' muted-sub'}`}>
            {formatMoney(loan.pastDuePrincipal)}
          </dd>
        </div>
        <div>
          <dt>Interest due</dt>
          <dd className={`num${loan.pastDueInterest > 0 ? ' tone-attention' : ' muted-sub'}`}>
            {formatMoney(loan.pastDueInterest)}
          </dd>
        </div>
        <div>
          <dt>Fees</dt>
          <dd className={`num${loan.accruedFees > 0 ? ' tone-attention' : ' muted-sub'}`}>
            {formatMoney(loan.accruedFees)}
          </dd>
        </div>
        <div>
          <dt>Total liability</dt>
          <dd className="num">{formatMoney(loan.totalLiability)}</dd>
        </div>
        <div>
          <dt>Status</dt>
          <dd>{loan.status}</dd>
        </div>
      </dl>
    </div>
  )
}

// Full detail dialog for one cash movement, opened by clicking a row in a "Cash movements" table. Contributes the
// dialog chrome (backdrop/Escape close, scroll lock, focus trap) and fetches the related order, trade, loan, or
// dividend breakdown on open, since the movement row itself carries only the type, amount, and cycle.
export function MoneyTransactionModal({ transaction, participantId, onClose }) {
  const dialogRef = useRef(null)
  const closeRef = useRef(null)
  const [detail, setDetail] = useState(null)
  const [error, setError] = useState(null)

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

  useEffect(() => {
    let active = true
    api
      .getMoneyTransactionDetail(participantId, transaction.id)
      .then((data) => {
        if (active) setDetail(data)
      })
      .catch(() => {
        if (active) setError('Could not load the movement details.')
      })
    return () => {
      active = false
    }
  }, [participantId, transaction.id])

  if (!transaction) {
    return null
  }

  const label = CASH_LABEL[transaction.type] ?? transaction.type
  const tone = CASH_TONE[transaction.type] ?? 'flat'
  const titleId = `cash-modal-title-${transaction.id}`
  const isDividend = transaction.type === 'Dividend'
  const companyLink = detail?.trade?.companyId ?? detail?.order?.companyId ?? null

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

  const hasRelated = detail && (detail.order || detail.trade || detail.loan || isDividend)

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
            <span className="command-label">Cash movement #{transaction.id}</span>
            <h2 className="command-name" id={titleId}>
              {label}
            </h2>
          </div>
          <strong className={`num tone-${tone}`}>{formatMoney(transaction.amount)}</strong>
        </header>

        <div className="modal-body">
          <dl className="modal-stats">
            <div>
              <dt>Amount</dt>
              <dd className="num">{formatMoney(transaction.amount)}</dd>
            </div>
            <div>
              <dt>Cycle</dt>
              <dd className="num">{detail?.cycleNumber != null ? `#${detail.cycleNumber}` : `#${transaction.createdInCycleId}`}</dd>
            </div>
            <div>
              <dt>Recorded</dt>
              <dd>{formatTimestamp(detail?.createdAt)}</dd>
            </div>
            <div>
              <dt>From</dt>
              <dd className="cell-ellipsis">
                {detail?.fromWhomId != null ? (
                  detail.fromWhomName ? (
                    <Link className="cell-link" to={`/traders/${detail.fromWhomId}`}>
                      {detail.fromWhomName}
                    </Link>
                  ) : (
                    `#${detail.fromWhomId}`
                  )
                ) : (
                  '—'
                )}
              </dd>
            </div>
          </dl>

          {detail?.description ? (
            <div className="modal-section">
              <span className="map-stat-label">Description</span>
              <p className="note note-sm">{detail.description}</p>
            </div>
          ) : null}

          {error ? (
            <p className="note note-sm">{error}</p>
          ) : detail == null ? (
            <p className="note note-sm">Loading details…</p>
          ) : (
            <>
              {detail.order || detail.trade ? <TradeDetail order={detail.order} trade={detail.trade} /> : null}
              {detail.loan ? <LoanDetail loan={detail.loan} /> : null}
              {isDividend ? <DividendBreakdown lines={detail.dividendBreakdown ?? []} /> : null}
              {!hasRelated ? <p className="note note-sm">This entry has no linked order, trade, or loan.</p> : null}
            </>
          )}
        </div>

        <footer className="modal-foot">
          <button type="button" className="btn" ref={closeRef} onClick={onClose}>
            Close
          </button>
          {companyLink != null ? (
            <Link className="btn btn-primary" to={`/companies/${companyLink}`} onClick={onClose}>
              Open company page
            </Link>
          ) : null}
        </footer>
      </div>
    </div>
  )
}
