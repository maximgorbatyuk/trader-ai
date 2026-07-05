import { useCallback, useEffect, useRef, useState } from 'react'
import { useOutletContext } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatMoney } from './format'
import { Panel } from './Panel'
import { CompanyModal } from './CompanyModal'
import { PlayerPanel } from './PlayerPanel'
import { MarketMapPanel } from './MarketMapPanel'
import { PercentButtons } from './PercentButtons'

const QUANTITY_PRESETS = [
  { label: '10%', value: 0.1 },
  { label: '25%', value: 0.25 },
  { label: '50%', value: 0.5 },
  { label: '75%', value: 0.75 },
  { label: '100%', value: 1 },
]

const POLL_INTERVAL_MS = 1000
const OPEN_STATUSES = new Set(['Open', 'PartiallyFilled'])

// A null participant is the share issuer's own offering (seeded company sell orders).
function traderName(id, byId) {
  if (id == null) return 'Issuer'
  return byId.get(id) ?? `#${id}`
}

// The main dashboard: market map (with the two latest news), the player control surface, and the order book.
// The market state, connection, and control actions come from the app shell through the outlet context; this
// page owns only its own data poll.
function App() {
  const { market, connected, ready, pending, actionError, runAction } = useOutletContext()
  const [companies, setCompanies] = useState([])
  const [participants, setParticipants] = useState([])
  const [orders, setOrders] = useState([])
  const [news, setNews] = useState([])
  const [crises, setCrises] = useState([])
  const [scienceInvestigations, setScienceInvestigations] = useState([])
  const [player, setPlayer] = useState(null)
  const [playerHoldingCompanyIds, setPlayerHoldingCompanyIds] = useState(() => new Set())
  const [mapModalCompanyId, setMapModalCompanyId] = useState(null)

  const loadAll = useCallback(async () => {
    try {
      const [companyData, participantData, orderData, newsData, crisisData, scienceData, playerData] =
        await Promise.all([
          api.getCompanies(),
          api.getParticipants(),
          api.getOrders('open'),
          api.getNews(10),
          api.getCrises(10),
          api.getScienceInvestigations(10),
          api.getPlayer(),
        ])

      setCompanies(companyData)
      setParticipants(participantData)
      setOrders(orderData)
      setNews(newsData)
      setCrises(crisisData)
      setScienceInvestigations(scienceData)
      setPlayer(playerData)

      if (playerData) {
        const holdings = await api.getHoldings(playerData.id)
        setPlayerHoldingCompanyIds(
          new Set(holdings.filter((holding) => holding.shares > 0).map((holding) => holding.companyId)),
        )
      } else {
        setPlayerHoldingCompanyIds(new Set())
      }
    } catch {
      // Keep the last known state when a refresh fails; the shell surfaces the offline status.
    }
  }, [])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll])

  const participantNameById = new Map(participants.map((participant) => [participant.id, participant.name]))
  const bankruptParticipantIds = new Set(
    participants.filter((participant) => participant.isBankrupt).map((participant) => participant.id),
  )
  const companyNameById = new Map(companies.map((company) => [company.id, company.name]))
  const companyPriceById = new Map(companies.map((company) => [company.id, company.currentPrice]))
  const openOrders = orders.filter((order) => OPEN_STATUSES.has(order.status))
  const mapModalCompany = companies.find((company) => company.id === mapModalCompanyId) ?? null

  return (
    <>
      <main className="main">
        {!ready ? (
          <ConnectingState />
        ) : (
          <>
            {!connected ? <OfflineBanner /> : null}

            {actionError ? (
              <div className="banner" role="alert">
                <strong>Action failed.</strong>
                <span>{actionError}</span>
              </div>
            ) : null}

            {market === null && connected ? <SeedPanel pending={pending} runAction={runAction} /> : null}

            {market !== null ? (
              <>
                <CrisisBanner crises={crises} currentCycleNumber={market.currentCycleNumber} />
                <ScienceBanner
                  investigations={scienceInvestigations}
                  currentCycleNumber={market.currentCycleNumber}
                />

                <div className="dashboard">
                  <MarketMapPanel
                    companies={companies}
                    participants={participants}
                    playerHoldingCompanyIds={playerHoldingCompanyIds}
                    lastDividendTotal={market.lastDividendTotal}
                    currentCycleNumber={market.currentCycleNumber}
                    news={news}
                    onSelectCompany={setMapModalCompanyId}
                  />

                  <PlayerPanel companies={companies} onSelectCompany={setMapModalCompanyId} />

                  <OrderBookPanel
                    orders={openOrders}
                    participantNameById={participantNameById}
                    bankruptParticipantIds={bankruptParticipantIds}
                    companyNameById={companyNameById}
                    companyPriceById={companyPriceById}
                    player={player}
                    playerHoldingCompanyIds={playerHoldingCompanyIds}
                    onTraded={loadAll}
                  />
                </div>
              </>
            ) : null}
          </>
        )}
      </main>

      {mapModalCompany ? (
        <CompanyModal
          company={mapModalCompany}
          participantNameById={participantNameById}
          onClose={() => setMapModalCompanyId(null)}
        />
      ) : null}
    </>
  )
}

function ConnectingState() {
  return (
    <section className="placeholder" aria-busy="true">
      <span className="spinner" aria-hidden="true" />
      <p>Connecting to the trader-ai backend…</p>
    </section>
  )
}

function OfflineBanner() {
  return (
    <div className="banner" role="alert">
      <strong>Backend offline.</strong>
      <span>Showing the last known state. Retrying every {POLL_INTERVAL_MS / 1000}s.</span>
    </div>
  )
}

function SeedPanel({ pending, runAction }) {
  return (
    <section className="panel seed">
      <div className="seed-body">
        <strong>No market running</strong>
        <p>Seed the demo market to create companies, participants, and the first cycle.</p>
        <button className="btn btn-primary" disabled={pending} onClick={() => runAction(api.seedMarket)}>
          Seed demo market
        </button>
      </div>
    </section>
  )
}

// A crisis is only shown in the banner while it is still fresh, so an old shock does not linger at the top.
const CRISIS_RECENT_CYCLES = 15

function crisisDropRange(crisis) {
  const percents = crisis.industries.map((link) => Number(link.impactPercent))
  if (percents.length === 0) return null
  return { min: Math.min(...percents), max: Math.max(...percents) }
}

function formatDropRange(crisis) {
  const range = crisisDropRange(crisis)
  if (!range) return ''
  return range.min === range.max
    ? `−${range.max.toFixed(1)}%`
    : `−${range.min.toFixed(1)}% to −${range.max.toFixed(1)}%`
}

function sectorLabel(count) {
  return count === 1 ? 'sector' : 'sectors'
}

function scienceGainRange(investigation) {
  const percents = investigation.industries.map((link) => Number(link.impactPercent))
  if (percents.length === 0) return null
  return { min: Math.min(...percents), max: Math.max(...percents) }
}

function formatGainRange(investigation) {
  const range = scienceGainRange(investigation)
  if (!range) return ''
  return range.min === range.max
    ? `+${range.max.toFixed(1)}%`
    : `+${range.min.toFixed(1)}% to +${range.max.toFixed(1)}%`
}

// A prominent alert for the most recent crisis, shown only while it is recent relative to the current cycle.
function CrisisBanner({ crises, currentCycleNumber }) {
  const latest = crises[0]
  if (!latest) return null
  if (
    currentCycleNumber != null &&
    currentCycleNumber - latest.triggeredInCycleNumber > CRISIS_RECENT_CYCLES
  ) {
    return null
  }

  const sectorCount = latest.industries.length
  return (
    <div className="crisis-banner" role="alert">
      <span className="crisis-banner-mark" aria-hidden="true">
        ⚠
      </span>
      <div className="crisis-banner-body">
        <p className="crisis-banner-head">
          <span className="crisis-scope">{latest.scope} crisis</span>
          <span className="crisis-banner-title">{latest.title}</span>
        </p>
        <p className="crisis-banner-meta num">
          {sectorCount} {sectorLabel(sectorCount)} · {formatDropRange(latest)} · cycle{' '}
          {latest.triggeredInCycleNumber}
        </p>
      </div>
    </div>
  )
}

// A green counterpart to the crisis banner for the most recent science investigation, shown only while it
// is still recent relative to the current cycle so an old breakthrough does not linger at the top.
function ScienceBanner({ investigations, currentCycleNumber }) {
  const latest = investigations[0]
  if (!latest) return null
  if (
    currentCycleNumber != null &&
    currentCycleNumber - latest.triggeredInCycleNumber > CRISIS_RECENT_CYCLES
  ) {
    return null
  }

  const sectorCount = latest.industries.length
  return (
    <div className="science-banner" role="status">
      <span className="science-banner-mark" aria-hidden="true">
        🔬
      </span>
      <div className="crisis-banner-body">
        <p className="crisis-banner-head">
          <span className="science-scope">Science breakthrough</span>
          <span className="crisis-banner-title">{latest.title}</span>
        </p>
        <p className="science-banner-meta num">
          {sectorCount} {sectorLabel(sectorCount)} · {formatGainRange(latest)} · cycle{' '}
          {latest.triggeredInCycleNumber}
        </p>
      </div>
    </div>
  )
}

const BUY_FILTER_OPTIONS = [
  { value: 'owned', label: 'Player owns' },
  { value: 'all', label: 'All' },
]

function OrderBookPanel({
  orders,
  participantNameById,
  bankruptParticipantIds,
  companyNameById,
  companyPriceById,
  player,
  playerHoldingCompanyIds,
  onTraded,
}) {
  const [tradeOrder, setTradeOrder] = useState(null)
  const [activeSide, setActiveSide] = useState('Buy')
  // The buy book defaults to the companies the player holds, so it opens on the demand for the player's own
  // positions; the filter widens it to the whole book on demand.
  const [buyFilter, setBuyFilter] = useState('owned')
  const tabRefs = useRef({})

  // Sells list highest price first. Buys surface the companies the player already holds first (so it can add
  // to or defend those positions), then everything else, each group ordered by highest price.
  const buysAll = orders
    .filter((order) => order.type === 'Buy')
    .sort((a, b) => {
      const aHeld = playerHoldingCompanyIds.has(a.companyId)
      const bHeld = playerHoldingCompanyIds.has(b.companyId)
      if (aHeld !== bHeld) return aHeld ? -1 : 1
      return b.limitPrice - a.limitPrice
    })
  const buys = buyFilter === 'owned' ? buysAll.filter((order) => playerHoldingCompanyIds.has(order.companyId)) : buysAll
  const sells = orders
    .filter((order) => order.type === 'Sell')
    .sort((a, b) => b.limitPrice - a.limitPrice)

  const sides = [
    { key: 'Buy', tone: 'up', orders: buys },
    { key: 'Sell', tone: 'down', orders: sells },
  ]
  const active = sides.find((side) => side.key === activeSide) ?? sides[0]

  function focusTab(key) {
    setActiveSide(key)
    tabRefs.current[key]?.focus()
  }

  function onTabKeyDown(event) {
    if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') return
    event.preventDefault()
    focusTab(activeSide === 'Buy' ? 'Sell' : 'Buy')
  }

  return (
    <Panel title="Order book" count={`${orders.length} open`} className="panel-book">
      <div className="book-tabs tabs" role="tablist" aria-label="Order book side" onKeyDown={onTabKeyDown}>
        {sides.map((side) => {
          const selected = side.key === activeSide
          return (
            <button
              key={side.key}
              type="button"
              role="tab"
              id={`booktab-${side.key}`}
              aria-selected={selected}
              aria-controls={`bookpanel-${side.key}`}
              tabIndex={selected ? 0 : -1}
              ref={(element) => {
                tabRefs.current[side.key] = element
              }}
              className={`tab${selected ? ' is-active' : ''}`}
              onClick={() => setActiveSide(side.key)}
            >
              {side.key}
              <span className="num book-tab-count">{side.orders.length}</span>
            </button>
          )
        })}
      </div>
      {active.key === 'Buy' ? (
        <div className="book-filter">
          <label className="filter-field">
            <span className="filter-label">Show</span>
            <select className="select select-sm" value={buyFilter} onChange={(event) => setBuyFilter(event.target.value)}>
              {BUY_FILTER_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
        </div>
      ) : null}
      <div className="tabpanel" role="tabpanel" id={`bookpanel-${active.key}`} aria-labelledby={`booktab-${active.key}`}>
        <OrderSide
          side={active.key}
          tone={active.tone}
          orders={active.orders}
          participantNameById={participantNameById}
          bankruptParticipantIds={bankruptParticipantIds}
          companyNameById={companyNameById}
          companyPriceById={companyPriceById}
          player={player}
          playerHoldingCompanyIds={playerHoldingCompanyIds}
          onTrade={setTradeOrder}
        />
      </div>
      {tradeOrder ? (
        <TradeOrderModal
          key={tradeOrder.id}
          order={tradeOrder}
          player={player}
          companyName={companyNameById.get(tradeOrder.companyId) ?? `#${tradeOrder.companyId}`}
          currentPrice={companyPriceById.get(tradeOrder.companyId) ?? null}
          onClose={() => setTradeOrder(null)}
          onTraded={onTraded}
        />
      ) : null}
    </Panel>
  )
}

function OrderSide({ side, tone, orders, participantNameById, bankruptParticipantIds, companyNameById, companyPriceById, player, playerHoldingCompanyIds, onTrade }) {
  if (orders.length === 0) {
    return <p className="note note-sm">No {side.toLowerCase()} orders.</p>
  }

  return (
    <div className="tbl-scroll">
      <table className="tbl tbl-book">
        <thead>
          <tr>
            <th scope="col" className="ta-r">
              Order price
            </th>
            <th scope="col" className="ta-r">
              Market price
            </th>
            <th scope="col" className="ta-r">
              Quantity
            </th>
            <th scope="col">Trader</th>
          </tr>
        </thead>
        <tbody>
          {orders.map((order) => {
            const isOwn = player != null && order.participantId === player.id
            const actionable = player != null && !isOwn
            const isBankrupt = !!bankruptParticipantIds?.has(order.participantId)
            const remaining = order.quantity - order.filledQuantity
            const companyName = companyNameById.get(order.companyId) ?? `#${order.companyId}`
            const marketPrice = companyPriceById.get(order.companyId) ?? null
            // How far the order's limit sits from the live market price, so the gap reads at a glance.
            const percentDiff =
              marketPrice != null && marketPrice > 0 ? ((order.limitPrice - marketPrice) / marketPrice) * 100 : null
            const diffLabel =
              percentDiff != null
                ? `${percentDiff > 0 ? '+' : percentDiff < 0 ? '−' : ''}${Math.abs(percentDiff).toFixed(1)}%`
                : null
            const diffGlyph = percentDiff == null ? '' : percentDiff > 0 ? '▲' : percentDiff < 0 ? '▼' : '◆'
            const diffTone = percentDiff == null || percentDiff === 0 ? 'flat' : percentDiff > 0 ? 'up' : 'down'
            // A bid the player can sell into: they hold shares of its company and it is not their own order.
            const sellable = side === 'Buy' && actionable && !!playerHoldingCompanyIds?.has(order.companyId)
            // The player takes the opposite side: buy a resting sell offer, sell into a resting buy order.
            const actionLabel =
              side === 'Sell'
                ? `Buy ${remaining} ${companyName} shares at ${formatMoney(order.limitPrice)}`
                : `Sell ${remaining} ${companyName} shares at ${formatMoney(order.limitPrice)}`
            const rowClass = `book-row${actionable ? ' is-actionable' : ''}${isOwn ? ' is-own' : ''}${sellable ? ' is-sellable' : ''}`
            const handlers = actionable
              ? {
                  role: 'button',
                  tabIndex: 0,
                  'aria-label': actionLabel,
                  title: side === 'Sell' ? 'Buy this offer' : 'Sell into this bid',
                  onClick: () => onTrade(order),
                  onKeyDown: (event) => {
                    if (event.key === 'Enter' || event.key === ' ') {
                      event.preventDefault()
                      onTrade(order)
                    }
                  },
                }
              : {}
            return (
              <tr key={order.id} className={rowClass} {...handlers}>
                <td className={`num ta-r tone-${tone}`}>
                  {formatMoney(order.limitPrice)}
                  {diffLabel ? (
                    <span className={`book-diff num tone-${diffTone}`} title="Order price versus current market price">
                      <span aria-hidden="true">{diffGlyph} </span>
                      {diffLabel}
                    </span>
                  ) : null}
                </td>
                <td className="num ta-r">{marketPrice != null ? formatMoney(marketPrice) : '—'}</td>
                <td className="num ta-r">
                  {remaining}
                  <span className="muted-sub">/{order.quantity}</span>
                </td>
                <td>
                  <span className="cell-trader">
                    <span className="cell-ellipsis">
                      {sellable ? (
                        <span className="book-hold" title="You hold shares to sell into this bid" aria-hidden="true">
                          ●{' '}
                        </span>
                      ) : null}
                      {isOwn ? 'You' : traderName(order.participantId, participantNameById)}
                      <span className="muted-sub"> · {companyName}</span>
                    </span>
                    {side === 'Sell' && isBankrupt ? <span className="tag tag-bankrupt">Bankrupt</span> : null}
                  </span>
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

// Confirms a player trade against one resting order. The player takes the opposite side at that order's
// limit; like every order it settles on the next cycle at the midpoint, so this books a competitive order
// rather than instantly hitting the shown counterparty.
function TradeOrderModal({ order, player, companyName, currentPrice, onClose, onTraded }) {
  const takingSellOffer = order.type === 'Sell'
  const playerSide = takingSellOffer ? 'Buy' : 'Sell'
  const remaining = order.quantity - order.filledQuantity
  // Selling into a bid needs the player's holding for this company; buying an offer does not (null = pending).
  const [ownedShares, setOwnedShares] = useState(takingSellOffer ? 0 : null)
  const [quantity, setQuantity] = useState(takingSellOffer ? String(remaining) : '')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState(null)

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

  // A sell can only offer shares the player holds, so seed the quantity with the most that both the holding
  // and the resting bid can absorb.
  useEffect(() => {
    if (takingSellOffer) return undefined

    let active = true
    api
      .getHoldings(player.id)
      .then((holdings) => {
        if (!active) return
        const holding = holdings.find((item) => item.companyId === order.companyId)
        const owned = holding ? holding.shares : 0
        setOwnedShares(owned)
        setQuantity(owned > 0 ? String(Math.min(owned, remaining)) : '')
      })
      .catch(() => {
        if (active) setOwnedShares(0)
      })
    return () => {
      active = false
    }
  }, [takingSellOffer, player.id, order.companyId, remaining])

  function onBackdropClick(event) {
    if (event.target === event.currentTarget) onClose()
  }

  const loadingHoldings = !takingSellOffer && ownedShares === null
  const noShares = !takingSellOffer && ownedShares === 0
  const maxQuantity = takingSellOffer ? remaining : Math.min(ownedShares ?? 0, remaining)
  const quantityNumber = Number(quantity)

  // Fill the quantity from a percentage of the max the bid/offer and holding allow, rounding up.
  function pickQuantity(fraction) {
    setQuantity(String(Math.ceil(maxQuantity * fraction)))
  }
  const validQuantity = Number.isInteger(quantityNumber) && quantityNumber > 0 && quantityNumber <= maxQuantity
  const total = (validQuantity ? quantityNumber : 0) * order.limitPrice

  // How far the order's limit sits from the live market price, so the player can judge the deal at a glance.
  const percentDiff =
    currentPrice != null && currentPrice > 0 ? ((order.limitPrice - currentPrice) / currentPrice) * 100 : null
  const diffLabel =
    percentDiff != null
      ? `${percentDiff > 0 ? '+' : percentDiff < 0 ? '−' : ''}${Math.abs(percentDiff).toFixed(1)}%`
      : null
  const diffGlyph = percentDiff == null ? '' : percentDiff > 0 ? '▲' : percentDiff < 0 ? '▼' : '◆'
  // The deal favors the player when they buy below market or sell above it, which drives the badge colour.
  const favorable =
    percentDiff == null || percentDiff === 0 ? null : takingSellOffer ? percentDiff < 0 : percentDiff > 0
  const diffTone = favorable == null ? 'flat' : favorable ? 'up' : 'down'

  async function handleSubmit(event) {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await api.placeOrder({
        participantId: player.id,
        companyId: order.companyId,
        type: playerSide,
        quantity: quantityNumber,
        limitPrice: order.limitPrice,
      })
      await onTraded()
      onClose()
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="modal-backdrop" onClick={onBackdropClick}>
      <div className="modal" role="dialog" aria-modal="true" aria-label="Trade against order">
        <header className="modal-head">
          <div className="command-id">
            <span className="command-label">Order book</span>
            <h2 className="command-name">{takingSellOffer ? 'Buy this offer' : 'Sell into this bid'}</h2>
          </div>
        </header>

        <form className="modal-body" onSubmit={handleSubmit}>
          <div className="trade-quote">
            <span className="map-stat-label">Order price</span>
            <div className="quote">
              <span className="quote-last num">{formatMoney(order.limitPrice)}</span>
              {diffLabel ? (
                <span className={`quote-change num tone-${diffTone}`} title="Order price versus current market price">
                  <span aria-hidden="true">{diffGlyph} </span>
                  {diffLabel} vs market
                </span>
              ) : null}
            </div>
          </div>

          <dl className="modal-stats">
            <div>
              <dt>Company</dt>
              <dd>{companyName}</dd>
            </div>
            <div>
              <dt>You</dt>
              <dd className={`tone-${takingSellOffer ? 'up' : 'down'}`}>
                <span aria-hidden="true">{takingSellOffer ? '▲' : '▼'} </span>
                {playerSide}
              </dd>
            </div>
            <div>
              <dt>Market price</dt>
              <dd className="num">{currentPrice != null ? formatMoney(currentPrice) : '—'}</dd>
            </div>
            <div>
              <dt>Total</dt>
              <dd className="num">{formatMoney(total)}</dd>
            </div>
          </dl>

          {loadingHoldings ? (
            <p className="note note-sm">Checking your holdings…</p>
          ) : noShares ? (
            <p className="note note-sm">You hold no {companyName} shares to sell.</p>
          ) : (
            <>
              <div className="field">
                <span>Quantity (max {maxQuantity})</span>
                <PercentButtons options={QUANTITY_PRESETS} ariaLabel="Set quantity from a percentage" onPick={pickQuantity} />
                <input
                  className="select num"
                  type="number"
                  min="1"
                  max={maxQuantity}
                  step="1"
                  aria-label="Quantity"
                  value={quantity}
                  onChange={(event) => setQuantity(event.target.value)}
                  autoFocus
                />
              </div>

              <p className="note note-sm">
                Places a matching {playerSide.toLowerCase()} order that fills on the next cycle at the midpoint price.
              </p>
            </>
          )}

          {error ? (
            <p className="command-error" role="alert">
              {error}
            </p>
          ) : null}

          <footer className="modal-foot">
            <button type="button" className="btn" onClick={onClose}>
              {noShares ? 'Close' : 'Cancel'}
            </button>
            {loadingHoldings || noShares ? null : (
              <button type="submit" className="btn btn-primary" disabled={submitting || !validQuantity}>
                {submitting ? 'Placing…' : `Place ${playerSide.toLowerCase()} order`}
              </button>
            )}
          </footer>
        </form>
      </div>
    </div>
  )
}

export default App
