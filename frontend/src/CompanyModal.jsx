import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from './api'
import { formatCompactMoney, formatInt, formatMoney, toneOf } from './format'
import { LineChart } from './LineChart'
import { OrderForm } from './OrderForm'
import { luldPresentation } from './marketAccounting'
import { RatingBadge } from './RatingBadge'
import { FavoriteCompanyToggle } from './FavoriteCompanyToggle'

const POLL_INTERVAL_MS = 1000

function formatPct(value) {
  if (typeof value !== 'number') return '—'
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  return `${sign}${(Math.abs(value) * 100).toFixed(2)}%`
}

function formatOwnershipPct(value) {
  if (typeof value !== 'number') return '—'
  const percentage = value * 100
  if (percentage > 0 && percentage < 0.01) return '<0.01%'
  return `${percentage.toFixed(2)}%`
}

// Sentiment is a small signed index; a leading + keeps its sign explicit, matching the industries page.
function formatSentiment(value) {
  if (typeof value !== 'number') return '—'
  return `${value > 0 ? '+' : ''}${value}`
}

function formatCyclesAgo(cyclesAgo) {
  if (typeof cyclesAgo !== 'number') return ''
  if (cyclesAgo <= 0) return 'this cycle'
  return `${formatInt(cyclesAgo)} cycle${cyclesAgo === 1 ? '' : 's'} ago`
}

// Detail dialog for one company opened from the market map. Live price, cap and share count come from the
// dashboard's already-polled company record; the price history and most recent trade are fetched here.
export function CompanyModal({ company, actorKind = 'player', onClose, onFavoriteChanged }) {
  const companyId = company?.id
  const [prices, setPrices] = useState([])
  const [player, setPlayer] = useState(null)
  const [industrySentiment, setIndustrySentiment] = useState([])
  const [latestRating, setLatestRating] = useState(null)
  const [activeForm, setActiveForm] = useState('none')
  const industryId = company?.industryId
  const dialogRef = useRef(null)
  const closeRef = useRef(null)

  useEffect(() => {
    if (companyId == null) return undefined

    let active = true
    async function load() {
      try {
        const [priceData, playerData, sentimentData, ratingData] = await Promise.all([
          api.getPrices(companyId),
          api.getPlayer(),
          industryId != null ? api.getIndustrySentimentHistory(industryId) : Promise.resolve([]),
          api.getCompanyRatings(companyId, 1),
        ])
        if (!active) return
        setPrices(priceData)
        setPlayer(playerData)
        setIndustrySentiment(sentimentData ?? [])
        setLatestRating((ratingData && ratingData[0]) ?? null)
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
  }, [companyId, industryId])

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

  const luld = luldPresentation(company.luldState)
  const isHalted = luld.orderEntryDisabled
  const fund =
    player?.fundParticipantId != null
      ? { id: player.fundParticipantId, name: player.fundName, availableBalance: player.fundAvailableBalance, margin: player.fundMargin }
      : null
  const ownedShares = company.playerPosition?.shares ?? 0
  const fundOwnedShares = company.fundPosition?.shares ?? 0
  const actorPosition = actorKind === 'fund' ? company.fundPosition : company.playerPosition
  const actorPositionLabel = actorKind === 'fund' ? 'Managed fund position' : 'Player position'
  const capitalization = company.issuedSharesCount * (company.currentPrice ?? 0)
  // The trend line charts capitalisation, not price, so a stock split (price down, shares up, cap flat) does
  // not read as a crash. Capitalisation is recorded going forward, so snapshots predating it are skipped.
  const capValues = prices
    .filter((snapshot) => snapshot.capitalization != null)
    .map((snapshot) => snapshot.capitalization)
  const capSeriesChange = capValues.length >= 2 ? capValues.at(-1) - capValues.at(0) : 0
  const sentimentValues = industrySentiment.map((point) => point.sentimentValue)
  const sentimentChange = sentimentValues.length >= 2 ? sentimentValues.at(-1) - sentimentValues.at(0) : 0
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
            {player ? (
              <FavoriteCompanyToggle
                companyId={company.id}
                companyName={company.name}
                isFavorite={company.isFavorite}
                onChanged={onFavoriteChanged}
              />
            ) : null}
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
              {luld.indicator} {luld.label}. {luld.executionNote}
            </p>
          ) : null}

          <div className="modal-charts">
            <div className="modal-section">
              <span className="map-stat-label">Capitalization</span>
              {capValues.length < 2 ? (
                <p className="note note-sm">Not enough capitalization history yet. Start the loop or step a cycle to record trades.</p>
              ) : (
                <LineChart
                  values={capValues.slice(-32)}
                  tone={toneOf(capSeriesChange)}
                  formatValue={formatCompactMoney}
                  label="Capitalization history"
                />
              )}
            </div>

            <div className="modal-section">
              <span className="map-stat-label">Industry sentiment · {company.industryName ?? '—'}</span>
              {sentimentValues.length < 2 ? (
                <p className="note note-sm">Not enough sentiment history yet.</p>
              ) : (
                <LineChart
                  values={sentimentValues.slice(-48)}
                  tone={toneOf(sentimentChange)}
                  formatValue={formatSentiment}
                  label={`${company.industryName ?? 'Industry'} sentiment history`}
                />
              )}
            </div>
          </div>

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
          </dl>

          <div className="modal-section">
            <span className="map-stat-label">{actorPositionLabel}</span>
            {(actorPosition?.shares ?? 0) > 0 ? (
              <dl className="modal-stats">
                <div>
                  <dt>Shares owned</dt>
                  <dd className="num">{formatInt(actorPosition.shares)}</dd>
                </div>
                <div>
                  <dt>Ownership</dt>
                  <dd className="num">{formatOwnershipPct(actorPosition.ownershipPct)}</dd>
                </div>
                <div>
                  <dt>Position value</dt>
                  <dd className="num">{formatMoney(actorPosition.marketValue)}</dd>
                </div>
              </dl>
            ) : (
              <p className="note note-sm">No shares of this company</p>
            )}
          </div>

          <div className="modal-section">
            <span className="map-stat-label">Latest risk estimation</span>
            {latestRating ? (
              <p className="modal-deal">
                <RatingBadge rating={latestRating.rating} impactPercent={latestRating.impactPercent} />
                <span className="muted-sub">
                  {' '}
                  · {latestRating.auditorName} · {formatCyclesAgo(latestRating.cyclesAgo)}
                </span>
              </p>
            ) : (
              <p className="note note-sm">No auditor has reviewed this company yet.</p>
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
