import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from './api'
import { formatInt, formatMoney, toneOf } from './format'
import { LineChart } from './LineChart'

const POLL_INTERVAL_MS = 1000

// A null seller is the share issuer's own offering.
function dealParty(id, byId) {
  if (id == null) return 'Issuer'
  return byId.get(id) ?? `#${id}`
}

function formatPct(value) {
  if (typeof value !== 'number') return '—'
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  return `${sign}${(Math.abs(value) * 100).toFixed(2)}%`
}

// Detail dialog for one company opened from the market map. Live price, cap and share count come from the
// dashboard's already-polled company record; the price history and most recent trade are fetched here.
export function CompanyModal({ company, participantNameById, onClose }) {
  const companyId = company?.id
  const [prices, setPrices] = useState([])
  const [latestDeal, setLatestDeal] = useState(null)
  const dialogRef = useRef(null)
  const closeRef = useRef(null)

  useEffect(() => {
    if (companyId == null) return undefined

    let active = true
    async function load() {
      try {
        const [priceData, dealData] = await Promise.all([
          api.getPrices(companyId),
          api.getCompanyShareTransactions(companyId, 1),
        ])
        if (!active) return
        setPrices(priceData)
        setLatestDeal(dealData[0] ?? null)
      } catch {
        // Keep the last known values when a refresh fails.
      }
    }

    load()
    const intervalId = setInterval(load, POLL_INTERVAL_MS)
    return () => {
      active = false
      clearInterval(intervalId)
    }
  }, [companyId])

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

  if (!company) {
    return null
  }

  const capitalization = company.issuedSharesCount * (company.currentPrice ?? 0)
  const values = prices.map((snapshot) => snapshot.price)
  const open = values.at(0)
  const last = values.at(-1)
  const low = values.length ? Math.min(...values) : undefined
  const high = values.length ? Math.max(...values) : undefined
  const seriesChange = values.length >= 2 ? last - open : 0
  const headlineTone = toneOf(company.priceChangePct)
  const titleId = `company-modal-title-${company.id}`

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
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        ref={dialogRef}
        onKeyDown={onDialogKeyDown}
      >
        <header className="modal-head">
          <div className="command-id">
            <span className="command-label">Company</span>
            <h2 className="command-name" id={titleId}>
              {company.name}
            </h2>
          </div>
          <div className="quote">
            <strong className="quote-last num">{formatMoney(company.currentPrice)}</strong>
            <span className={`quote-change num tone-${headlineTone}`}>
              <span aria-hidden="true">
                {headlineTone === 'up' ? '▲' : headlineTone === 'down' ? '▼' : '◆'}
              </span>
              {formatPct(company.priceChangePct)}
            </span>
          </div>
        </header>

        <div className="modal-body">
          {values.length < 2 ? (
            <p className="note">Not enough price history yet. Start the loop or step a cycle to record trades.</p>
          ) : (
            <LineChart values={values.slice(-32)} tone={toneOf(seriesChange)} />
          )}

          <dl className="modal-stats">
            <div>
              <dt>Industry</dt>
              <dd>{company.industryName ?? '—'}</dd>
            </div>
            <div>
              <dt>Capitalization</dt>
              <dd className="num">{formatMoney(capitalization)}</dd>
            </div>
            <div>
              <dt>Shares</dt>
              <dd className="num">{formatInt(company.issuedSharesCount)}</dd>
            </div>
            <div>
              <dt>Open</dt>
              <dd className="num">{formatMoney(open)}</dd>
            </div>
            <div>
              <dt>Low</dt>
              <dd className="num">{formatMoney(low)}</dd>
            </div>
            <div>
              <dt>High</dt>
              <dd className="num">{formatMoney(high)}</dd>
            </div>
          </dl>

          <div className="modal-section">
            <span className="map-stat-label">Latest deal</span>
            {latestDeal ? (
              <p className="modal-deal">
                <span className="cell-ellipsis">{dealParty(latestDeal.sellerId, participantNameById)}</span>
                <span className="flow-arrow" aria-label="to">
                  →
                </span>
                <span className="cell-ellipsis">{dealParty(latestDeal.buyerId, participantNameById)}</span>
                <span className="muted-sub">
                  {' '}
                  · {formatInt(latestDeal.quantity)} @ {formatMoney(latestDeal.price)}
                </span>
              </p>
            ) : (
              <p className="note">No trades for this company yet.</p>
            )}
          </div>
        </div>

        <footer className="modal-foot">
          <button type="button" className="btn" ref={closeRef} onClick={onClose}>
            Close
          </button>
          <Link className="btn btn-primary" to={`/companies/${company.id}`}>
            Open page
          </Link>
        </footer>
      </div>
    </div>
  )
}
