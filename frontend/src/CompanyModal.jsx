import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from './api'
import { formatCompactMoney, formatInt, formatMoney, toneOf } from './format'
import { LineChart } from './LineChart'
import { NewsImpact } from './NewsImpact'
import { OrderForm } from './OrderForm'

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
  const [player, setPlayer] = useState(null)
  const [ownedShares, setOwnedShares] = useState(0)
  const [fundOwnedShares, setFundOwnedShares] = useState(0)
  const [companyNews, setCompanyNews] = useState([])
  const [activeForm, setActiveForm] = useState('none')
  const dialogRef = useRef(null)
  const closeRef = useRef(null)

  useEffect(() => {
    if (companyId == null) return undefined

    let active = true
    async function load() {
      try {
        const [priceData, dealData, playerData, newsData] = await Promise.all([
          api.getPrices(companyId),
          api.getCompanyShareTransactions(companyId, 1),
          api.getPlayer(),
          api.getCompanyNews(companyId, 5),
        ])
        if (!active) return
        setPrices(priceData)
        setLatestDeal(dealData[0] ?? null)
        setPlayer(playerData)
        setCompanyNews(newsData ?? [])

        if (playerData) {
          const holdings = await api.getHoldings(playerData.id)
          if (!active) return
          const holding = holdings.find((item) => item.companyId === companyId)
          setOwnedShares(holding ? holding.shares : 0)

          if (playerData.fundParticipantId != null) {
            const fundHoldings = await api.getHoldings(playerData.fundParticipantId)
            if (!active) return
            const fundHolding = fundHoldings.find((item) => item.companyId === companyId)
            setFundOwnedShares(fundHolding ? fundHolding.shares : 0)
          } else {
            setFundOwnedShares(0)
          }
        } else {
          setOwnedShares(0)
          setFundOwnedShares(0)
        }
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

  const isHalted = company.isHalted
  const fund =
    player?.fundParticipantId != null
      ? { id: player.fundParticipantId, name: player.fundName, availableBalance: player.fundAvailableBalance }
      : null
  const capitalization = company.issuedSharesCount * (company.currentPrice ?? 0)
  const values = prices.map((snapshot) => snapshot.price)
  const open = values.at(0)
  const low = values.length ? Math.min(...values) : undefined
  const high = values.length ? Math.max(...values) : undefined
  // The trend line charts capitalisation, not price, so a stock split (price down, shares up, cap flat) does
  // not read as a crash. Capitalisation is recorded going forward, so snapshots predating it are skipped.
  const capValues = prices
    .filter((snapshot) => snapshot.capitalization != null)
    .map((snapshot) => snapshot.capitalization)
  const capSeriesChange = capValues.length >= 2 ? capValues.at(-1) - capValues.at(0) : 0
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
        className="modal modal-company"
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
          {isHalted ? (
            <p className="note" role="status">
              Trading in this company is halted after a sharp price move. Open orders were cancelled and none can
              be placed until it resumes.
            </p>
          ) : null}

          {capValues.length < 2 ? (
            <p className="note">Not enough capitalization history yet. Start the loop or step a cycle to record trades.</p>
          ) : (
            <LineChart
              values={capValues.slice(-32)}
              tone={toneOf(capSeriesChange)}
              formatValue={formatCompactMoney}
              label="Capitalization history"
            />
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

          <div className="modal-section">
            <span className="map-stat-label">Related news</span>
            {companyNews.length === 0 ? (
              <p className="note note-sm">No related news yet.</p>
            ) : (
              <ul className="news-mini">
                {companyNews.map((post) => (
                  <li key={post.id} className="news-mini-item">
                    <span className="news-mini-title cell-ellipsis">{post.title}</span>
                    <NewsImpact post={post} />
                  </li>
                ))}
              </ul>
            )}
          </div>

          {player && !isHalted && activeForm === 'buy' ? (
            <OrderForm key={`buy-${company.id}`} player={player} fund={fund} company={company} side="Buy" />
          ) : null}
          {player && !isHalted && activeForm === 'sell' ? (
            <OrderForm
              key={`sell-${company.id}`}
              player={player}
              fund={fund}
              company={company}
              side="Sell"
              playerMaxQuantity={ownedShares}
              fundMaxQuantity={fundOwnedShares}
            />
          ) : null}
        </div>

        <footer className="modal-foot">
          <button type="button" className="btn" ref={closeRef} onClick={onClose}>
            Close
          </button>
          <Link className="btn" to={`/companies/${company.id}`}>
            Open company page
          </Link>
          {player && !isHalted ? (
            <button
              type="button"
              className="btn btn-primary"
              aria-expanded={activeForm === 'buy'}
              onClick={() => setActiveForm((current) => (current === 'buy' ? 'none' : 'buy'))}
            >
              Buy shares
            </button>
          ) : null}
          {player && !isHalted && (ownedShares > 0 || fundOwnedShares > 0) ? (
            <button
              type="button"
              className="btn"
              aria-expanded={activeForm === 'sell'}
              onClick={() => setActiveForm((current) => (current === 'sell' ? 'none' : 'sell'))}
            >
              Sell shares
            </button>
          ) : null}
        </footer>
      </div>
    </div>
  )
}
