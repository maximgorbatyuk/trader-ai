import { useEffect, useRef, useState } from 'react'
import { api } from './api'
import { formatMoney } from './format'
import { Panel } from './Panel'
import { PercentButtons } from './PercentButtons'

const QUANTITY_PRESETS = [
  { label: '10%', value: 0.1 },
  { label: '25%', value: 0.25 },
  { label: '50%', value: 0.5 },
  { label: '75%', value: 0.75 },
  { label: '100%', value: 1 },
]

const BUY_FILTER_OPTIONS = [
  { value: 'owned', label: 'Owned' },
  { value: 'all', label: 'All' },
]

// Sell-book filters surface the two distress supplies: bankrupt traders dumping cheaply, and companies
// (the null-participant issuer float) offering their own shares.
const SELL_FILTER_OPTIONS = [
  { value: 'all', label: 'All' },
  { value: 'bankrupts', label: 'Bankrupts only' },
  { value: 'companies', label: 'Companies' },
]

// A null participant is the share issuer's own offering (seeded company sell orders).
function traderName(id, byId) {
  if (id == null) return 'Issuer'
  return byId.get(id) ?? `#${id}`
}

// The resting order book as a Buy/Sell tab pair. Shared by the dashboard and the Trade market page; each caller
// passes the already-derived lookups, the active actor (the player or their managed fund), and its own
// onSelectCompany (modal on the dashboard, route navigation on the trade page). The inner table scrolls, so a
// long book stays contained.
export function OrderBookPanel({
  orders,
  participantNameById,
  bankruptParticipantIds,
  companyNameById,
  companyPriceById,
  companySharesById,
  actor,
  actorHoldingCompanyIds,
  emptyActorHint,
  onSelectCompany,
  onTraded,
}) {
  const [tradeOrder, setTradeOrder] = useState(null)
  const [activeSide, setActiveSide] = useState('Buy')
  // The buy book defaults to the companies the actor holds, so it opens on the demand for the actor's own
  // positions; the filter widens it to the whole book on demand.
  const [buyFilter, setBuyFilter] = useState('owned')
  const [sellFilter, setSellFilter] = useState('all')
  const tabRefs = useRef({})

  // Sells list highest price first. Buys surface the companies the actor already holds first (so it can add
  // to or defend those positions), then everything else, each group ordered by highest price.
  const buysAll = orders
    .filter((order) => order.type === 'Buy')
    .sort((a, b) => {
      const aHeld = actorHoldingCompanyIds.has(a.companyId)
      const bHeld = actorHoldingCompanyIds.has(b.companyId)
      if (aHeld !== bHeld) return aHeld ? -1 : 1
      return b.limitPrice - a.limitPrice
    })
  const buys = buyFilter === 'owned' ? buysAll.filter((order) => actorHoldingCompanyIds.has(order.companyId)) : buysAll
  const sellsAll = orders
    .filter((order) => order.type === 'Sell')
    .sort((a, b) => b.limitPrice - a.limitPrice)
  const sells =
    sellFilter === 'bankrupts'
      ? sellsAll.filter((order) => bankruptParticipantIds.has(order.participantId))
      : sellFilter === 'companies'
        ? sellsAll.filter((order) => order.participantId == null)
        : sellsAll

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
      <div className="book-filter">
        <label className="filter-field">
          <span className="filter-label">Show</span>
          {active.key === 'Buy' ? (
            <select className="select select-sm" value={buyFilter} onChange={(event) => setBuyFilter(event.target.value)}>
              {BUY_FILTER_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          ) : (
            <select className="select select-sm" value={sellFilter} onChange={(event) => setSellFilter(event.target.value)}>
              {SELL_FILTER_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          )}
        </label>
      </div>
      {actor == null && emptyActorHint ? <p className="note note-sm">{emptyActorHint}</p> : null}
      <div className="tabpanel" role="tabpanel" id={`bookpanel-${active.key}`} aria-labelledby={`booktab-${active.key}`}>
        <OrderSide
          side={active.key}
          tone={active.tone}
          orders={active.orders}
          participantNameById={participantNameById}
          bankruptParticipantIds={bankruptParticipantIds}
          companyNameById={companyNameById}
          companyPriceById={companyPriceById}
          companySharesById={companySharesById}
          actor={actor}
          actorHoldingCompanyIds={actorHoldingCompanyIds}
          onSelectCompany={onSelectCompany}
          onTrade={setTradeOrder}
        />
      </div>
      {tradeOrder ? (
        <TradeOrderModal
          key={tradeOrder.id}
          order={tradeOrder}
          actor={actor}
          companyName={companyNameById.get(tradeOrder.companyId) ?? `#${tradeOrder.companyId}`}
          currentPrice={companyPriceById.get(tradeOrder.companyId) ?? null}
          onClose={() => setTradeOrder(null)}
          onTraded={onTraded}
        />
      ) : null}
    </Panel>
  )
}

function OrderSide({ side, tone, orders, participantNameById, bankruptParticipantIds, companyNameById, companyPriceById, companySharesById, actor, actorHoldingCompanyIds, onSelectCompany, onTrade }) {
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
            <th scope="col">Company</th>
            <th scope="col">Trader</th>
          </tr>
        </thead>
        <tbody>
          {orders.map((order) => {
            const isOwn = actor != null && order.participantId === actor.id
            const actionable = actor != null && !isOwn
            const isBankrupt = !!bankruptParticipantIds?.has(order.participantId)
            const remaining = order.quantity - order.filledQuantity
            const companyName = companyNameById.get(order.companyId) ?? `#${order.companyId}`
            const issuedShares = companySharesById?.get(order.companyId) ?? null
            // Share of the whole company still on offer, shown only for sells (a bid can exceed the float, so
            // the fraction is meaningless on the buy side).
            const sharePct =
              side === 'Sell' && issuedShares != null && issuedShares > 0
                ? (remaining / issuedShares) * 100
                : null
            const sharePctLabel =
              sharePct == null ? null : sharePct >= 0.1 ? `${sharePct.toFixed(1)}%` : '<0.1%'
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
            // A bid the actor can sell into: they hold shares of its company and it is not their own order.
            const sellable = side === 'Buy' && actionable && !!actorHoldingCompanyIds?.has(order.companyId)
            // The actor takes the opposite side: buy a resting sell offer, sell into a resting buy order.
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
                  {sharePctLabel ? (
                    <span className="book-sharepct muted-sub" title="Share of the company's issued shares on offer">
                      {sharePctLabel} of co.
                    </span>
                  ) : null}
                </td>
                <td>
                  <button
                    type="button"
                    className="cell-name-btn cell-ellipsis"
                    onClick={(event) => {
                      event.stopPropagation()
                      onSelectCompany?.(order.companyId)
                    }}
                    onKeyDown={(event) => event.stopPropagation()}
                    title={`Open ${companyName}`}
                  >
                    {companyName}
                  </button>
                </td>
                <td>
                  <span className="cell-trader">
                    <span className="cell-ellipsis cell-trader-name">
                      {sellable ? (
                        <span className="book-hold" title="You hold shares to sell into this bid" aria-hidden="true">
                          ●{' '}
                        </span>
                      ) : null}
                      {isOwn ? 'You' : traderName(order.participantId, participantNameById)}
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

// Confirms an actor's trade against one resting order. The actor (the player or their managed fund) takes the
// opposite side at that order's limit; like every order it settles on the next cycle at the midpoint, so this
// books a competitive order rather than instantly hitting the shown counterparty.
function TradeOrderModal({ order, actor, companyName, currentPrice, onClose, onTraded }) {
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
      .getHoldings(actor.id)
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
  }, [takingSellOffer, actor.id, order.companyId, remaining])

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
        participantId: actor.id,
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
