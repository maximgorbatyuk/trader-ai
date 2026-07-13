import { useEffect, useRef, useState } from 'react'
import { api } from './api'
import { formatMoney, formatSigned, toneOf } from './format'
import { LineChart } from './LineChart'
import { Panel } from './Panel'
import { PercentButtons } from './PercentButtons'
import { affordability } from './marginModel'
import { luldPresentation } from './marketAccounting'
import { classifyOrderPrice, orderPriceBounds } from './orderPriceRange'
import {
  recentPriceValues,
  recentSentimentValues,
  TRADE_QUANTITY_PRESETS,
  tradeOrderAvailability,
  tradeOrderEligibility,
} from './tradeOrderModalModel'

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
  companyById,
  actor,
  actorHoldingCompanyIds,
  actorHoldingByCompany,
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
          companyById={companyById}
          actor={actor}
          actorHoldingCompanyIds={actorHoldingCompanyIds}
          actorHoldingByCompany={actorHoldingByCompany}
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
          company={companyById.get(tradeOrder.companyId) ?? null}
          onClose={() => setTradeOrder(null)}
          onTraded={onTraded}
        />
      ) : null}
    </Panel>
  )
}

function OrderSide({ side, tone, orders, participantNameById, bankruptParticipantIds, companyNameById, companyPriceById, companySharesById, companyById, actor, actorHoldingCompanyIds, actorHoldingByCompany, onSelectCompany, onTrade }) {
  if (orders.length === 0) {
    return <p className="note note-sm">No {side.toLowerCase()} orders.</p>
  }

  // Gain/loss only applies to bids the actor can sell into; a resting sell offer has no cost basis for the actor.
  const showGainLoss = side === 'Buy'

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
            {showGainLoss ? (
              <th scope="col" className="ta-r">
                Gain/loss
              </th>
            ) : null}
            <th scope="col">Company</th>
            <th scope="col">Trader</th>
            <th scope="col">Action</th>
          </tr>
        </thead>
        <tbody>
          {orders.map((order) => {
            const isOwn = actor != null && order.participantId === actor.id
            const company = companyById?.get(order.companyId)
            const luld = luldPresentation(company?.luldState)
            const luldAffected = luld.orderEntryDisabled
            const bounds = orderPriceBounds(company)
            const placement = classifyOrderPrice(order.limitPrice, bounds)
            // Any resting order outside the executable band cannot be crossed directly; the label distinguishes a
            // valid waiting order from the defensive beyond-range case. Fall back to a band-only check when a
            // company response predates the allowed-range fields.
            const outsideBand = bounds.available
              ? placement === 'waiting' || placement === 'outside'
              : typeof company?.lowerBandPrice === 'number' &&
                typeof company?.upperBandPrice === 'number' &&
                (order.limitPrice < company.lowerBandPrice || order.limitPrice > company.upperBandPrice)
            const bandStatusLabel =
              placement === 'waiting'
                ? 'Waiting outside band'
                : placement === 'outside'
                  ? 'Outside allowed range'
                  : !bounds.available && outsideBand
                    ? 'Outside band'
                    : null
            const remaining = order.quantity - order.filledQuantity
            const availability = tradeOrderAvailability({
              actorId: actor?.id ?? null,
              orderParticipantId: order.participantId,
              remaining,
              price: order.limitPrice,
              company,
            })
            const actionable = availability.eligible
            const isBankrupt = !!bankruptParticipantIds?.has(order.participantId)
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
            // Realized gain/loss from selling the shares the actor holds into this bid: the most both the
            // holding and the bid can absorb, valued at the bid price minus the weighted-average price paid.
            const holding = actorHoldingByCompany?.get(order.companyId)
            const sellableQuantity = holding ? Math.min(holding.shares, remaining) : 0
            const sellGainLoss =
              sellable && holding && sellableQuantity > 0
                ? sellableQuantity * (order.limitPrice - holding.averageCost)
                : null
            const gainGlyph = sellGainLoss == null ? '' : sellGainLoss > 0 ? '▲' : sellGainLoss < 0 ? '▼' : '◆'
            // The actor takes the opposite side: buy a resting sell offer, sell into a resting buy order.
            const actionLabel =
              side === 'Sell'
                ? `Buy ${remaining} ${companyName} shares at ${formatMoney(order.limitPrice)}`
                : `Sell ${remaining} ${companyName} shares at ${formatMoney(order.limitPrice)}`
            const rowLabel = `Open order details. ${actionLabel}.${actionable ? '' : ` ${availability.reason}`}`
            const rowClass = `book-row tbl-row-click${actionable ? ' is-actionable' : ''}${isOwn ? ' is-own' : ''}${sellable ? ' is-sellable' : ''}`
            return (
              <tr
                key={order.id}
                className={rowClass}
                role="button"
                tabIndex={0}
                aria-label={rowLabel}
                onClick={() => onTrade(order)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault()
                    onTrade(order)
                  }
                }}
              >
                <td className={`num ta-r tone-${tone}`}>
                  {formatMoney(order.limitPrice)}
                  {diffLabel ? (
                    <span className={`book-diff num tone-${diffTone}`} title="Order price versus current market price">
                      <span aria-hidden="true">{diffGlyph} </span>
                      {diffLabel}
                    </span>
                  ) : null}
                </td>
                <td className="num ta-r">
                  {marketPrice != null ? formatMoney(marketPrice) : '—'}
                  {typeof company?.lowerBandPrice === 'number' && typeof company?.upperBandPrice === 'number' ? (
                    <span className="book-diff muted-sub">
                      Band {formatMoney(company.lowerBandPrice)}–{formatMoney(company.upperBandPrice)}
                    </span>
                  ) : null}
                </td>
                <td className="num ta-r">
                  {remaining}
                  <span className="muted-sub">/{order.quantity}</span>
                  {sharePctLabel ? (
                    <span className="book-sharepct muted-sub" title="Share of the company's issued shares on offer">
                      {sharePctLabel} of co.
                    </span>
                  ) : null}
                </td>
                {showGainLoss ? (
                  <td className="num ta-r">
                    {sellGainLoss != null ? (
                      <span className={`num tone-${toneOf(sellGainLoss)}`} title="Gain or loss from selling your shares into this bid">
                        <span aria-hidden="true">{gainGlyph} </span>
                        {formatSigned(sellGainLoss)}
                      </span>
                    ) : (
                      <span className="muted-sub">—</span>
                    )}
                  </td>
                ) : null}
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
                    {bandStatusLabel ? <span className="tag tag-flag">{bandStatusLabel}</span> : null}
                    {luldAffected ? <span className="tag tag-flag">{luld.indicator} {luld.label}</span> : null}
                  </span>
                </td>
                <td>
                  {actionable ? (
                    <button
                      type="button"
                      className="btn"
                      aria-label={actionLabel}
                      title={side === 'Sell' ? 'Buy this offer' : 'Sell into this bid'}
                      onClick={(event) => {
                        event.stopPropagation()
                        onTrade(order)
                      }}
                      onKeyDown={(event) => event.stopPropagation()}
                    >
                      {side === 'Sell' ? 'Buy' : 'Sell'}
                    </button>
                  ) : (
                    <span className="muted-sub">—</span>
                  )}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

// Confirms an actor's trade against one resting order. It books a crossing order for the next matching pass;
// trade-date balances update at a fill while cash and securities settle on the next trading day.
function formatSentiment(value) {
  if (typeof value !== 'number') return '—'
  return `${value > 0 ? '+' : ''}${value}`
}

function TradeOrderModal({ order, actor, companyName, currentPrice, company, onClose, onTraded }) {
  const takingSellOffer = order.type === 'Sell'
  const playerSide = takingSellOffer ? 'Buy' : 'Sell'
  const remaining = order.quantity - order.filledQuantity
  const actorId = actor?.id ?? null
  const availability = tradeOrderAvailability({
    actorId,
    orderParticipantId: order.participantId,
    remaining,
    price: order.limitPrice,
    company,
  })
  const capacity = affordability(actor?.availableBalance ?? 0, actor?.buyingPower ?? 0, order.limitPrice)
  const initialBuyQuantity = availability.eligible ? Math.min(remaining, capacity.marginShares) : 0
  const luld = luldPresentation(company?.luldState)
  // Selling into a bid needs the player's holding for this company; buying an offer does not (null = pending).
  const [ownedShares, setOwnedShares] = useState(takingSellOffer ? 0 : null)
  // Weighted-average price the actor paid per held share, used to show the realized gain/loss on a sell.
  const [averageCost, setAverageCost] = useState(null)
  const [quantity, setQuantity] = useState(takingSellOffer && initialBuyQuantity > 0 ? String(initialBuyQuantity) : '')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState(null)
  const [priceHistory, setPriceHistory] = useState(null)
  const [sentimentHistory, setSentimentHistory] = useState(null)
  const [priceHistoryFailed, setPriceHistoryFailed] = useState(false)
  const [sentimentHistoryFailed, setSentimentHistoryFailed] = useState(false)
  const dialogRef = useRef(null)
  const closeRef = useRef(null)

  useEffect(() => {
    const previouslyFocused = document.activeElement
    function onKeyDown(event) {
      if (event.key === 'Escape') onClose()
    }

    document.addEventListener('keydown', onKeyDown)
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    closeRef.current?.focus()
    return () => {
      document.removeEventListener('keydown', onKeyDown)
      document.body.style.overflow = previousOverflow
      if (previouslyFocused instanceof HTMLElement) previouslyFocused.focus()
    }
  }, [onClose])

  useEffect(() => {
    let active = true

    async function loadChartData() {
      const [pricesResult, sentimentResult] = await Promise.allSettled([
        api.getPrices(order.companyId),
        company?.industryId != null ? api.getIndustrySentimentHistory(company.industryId) : Promise.resolve([]),
      ])
      if (!active) return

      setPriceHistory(pricesResult.status === 'fulfilled' ? pricesResult.value ?? [] : [])
      setSentimentHistory(sentimentResult.status === 'fulfilled' ? sentimentResult.value ?? [] : [])
      setPriceHistoryFailed(pricesResult.status === 'rejected')
      setSentimentHistoryFailed(sentimentResult.status === 'rejected')
    }

    loadChartData()
    return () => {
      active = false
    }
  }, [order.companyId, company?.industryId])

  // A sell can only offer shares the player holds, so seed the quantity with the most that both the holding
  // and the resting bid can absorb.
  useEffect(() => {
    if (takingSellOffer || !availability.eligible || actorId == null) return undefined

    let active = true
    api
      .getHoldings(actorId)
      .then((holdings) => {
        if (!active) return
        const holding = holdings.find((item) => item.companyId === order.companyId)
        const owned = holding ? holding.shares : 0
        setOwnedShares(owned)
        setAverageCost(holding && holding.shares > 0 ? holding.costBasis / holding.shares : null)
        setQuantity(owned > 0 ? String(Math.min(owned, remaining)) : '')
      })
      .catch(() => {
        if (active) setOwnedShares(0)
      })
    return () => {
      active = false
    }
  }, [takingSellOffer, availability.eligible, actorId, order.companyId, remaining])

  function onBackdropClick(event) {
    if (event.target === event.currentTarget) onClose()
  }

  const loadingHoldings = availability.eligible && !takingSellOffer && ownedShares === null
  const noShares = availability.eligible && !takingSellOffer && ownedShares === 0
  const maxQuantity = !availability.eligible
    ? 0
    : takingSellOffer
      ? Math.min(remaining, capacity.marginShares)
      : Math.min(ownedShares ?? 0, remaining)
  const noBuyingPower = availability.eligible && takingSellOffer && maxQuantity === 0
  const quantityNumber = Number(quantity)

  // Fill the quantity from a percentage of the max the bid/offer and holding allow, rounding up.
  function pickQuantity(fraction) {
    setQuantity(String(Math.ceil(maxQuantity * fraction)))
  }
  const eligibility = tradeOrderEligibility({
    actorId,
    orderParticipantId: order.participantId,
    remaining,
    company,
    side: playerSide,
    quantity: quantityNumber,
    price: order.limitPrice,
    ownedShares,
    buyingPower: actor?.buyingPower ?? 0,
  })
  const validQuantity = eligibility.eligible
  const total = (validQuantity ? quantityNumber : 0) * order.limitPrice
  // When selling into a bid, weigh the average price paid for the shares being sold against the order's
  // proceeds so the realized gain or loss on this lot reads directly.
  const sellQuantity = validQuantity ? quantityNumber : 0
  const soldCostBasis = !takingSellOffer && averageCost != null && sellQuantity > 0 ? averageCost * sellQuantity : null
  const estimatedGain = soldCostBasis != null ? total - soldCostBasis : null
  const gainGlyph = estimatedGain == null ? '' : estimatedGain > 0 ? '▲' : estimatedGain < 0 ? '▼' : '◆'
  const priceValues = recentPriceValues(priceHistory ?? [])
  const sentimentValues = recentSentimentValues(sentimentHistory ?? [])
  const priceChange = priceValues.length >= 2 ? priceValues.at(-1) - priceValues.at(0) : 0
  const sentimentChange = sentimentValues.length >= 2 ? sentimentValues.at(-1) - sentimentValues.at(0) : 0

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
    if (!validQuantity || actorId == null) return
    setError(null)
    setSubmitting(true)
    try {
      await api.placeOrder({
        participantId: actorId,
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

  function onDialogKeyDown(event) {
    if (event.key !== 'Tab') return
    const focusable = dialogRef.current?.querySelectorAll(
      'a[href], button:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])',
    )
    if (!focusable || focusable.length === 0) return
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault()
      last.focus()
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault()
      first.focus()
    }
  }

  const titleId = `trade-order-modal-title-${order.id}`

  return (
    <div className="modal-backdrop" onClick={onBackdropClick}>
      <div
        className="modal modal-trade"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        ref={dialogRef}
        onKeyDown={onDialogKeyDown}
      >
        <header className="modal-head">
          <div className="command-id">
            <span className="command-label">Order book</span>
            <h2 className="command-name" id={titleId}>{takingSellOffer ? 'Buy this offer' : 'Sell into this bid'}</h2>
          </div>
        </header>

        <form className="modal-body" onSubmit={handleSubmit}>
          <div className="trade-quote">
            <div className="trade-quote-main">
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
            {soldCostBasis != null ? (
              <div className="trade-gain">
                <span className="map-stat-label">Est. gain/loss</span>
                <div className={`trade-gain-value num tone-${toneOf(estimatedGain)}`} title="Order proceeds minus what you paid for these shares">
                  <span aria-hidden="true">{gainGlyph} </span>
                  {formatSigned(estimatedGain)}
                </div>
              </div>
            ) : null}
          </div>

          <div className="trade-charts">
            <section className="trade-chart" aria-labelledby="trade-price-chart-title">
              <div className="trade-chart-head">
                <h3 id="trade-price-chart-title">Company share price change</h3>
                <span className={`trade-chart-change num tone-${toneOf(priceChange)}`}>
                  <span aria-hidden="true">{priceChange > 0 ? '▲' : priceChange < 0 ? '▼' : '◆'} </span>
                  {formatMoney(priceValues.at(-1))}
                </span>
              </div>
              <p className="trade-chart-context">{companyName} · Latest 48 cycles</p>
              {priceHistory === null ? (
                <p className="note note-sm">Loading price history…</p>
              ) : priceHistoryFailed ? (
                <p className="note note-sm">Price history is unavailable.</p>
              ) : priceValues.length < 2 ? (
                <p className="note note-sm">Not enough price history yet.</p>
              ) : (
                <LineChart
                  values={priceValues}
                  tone={toneOf(priceChange)}
                  formatValue={formatMoney}
                  label={`${companyName} share price history`}
                />
              )}
            </section>

            <section className="trade-chart" aria-labelledby="trade-sentiment-chart-title">
              <div className="trade-chart-head">
                <h3 id="trade-sentiment-chart-title">Industry sentiment change</h3>
                <span className={`trade-chart-change num tone-${toneOf(sentimentChange)}`}>
                  <span aria-hidden="true">{sentimentChange > 0 ? '▲' : sentimentChange < 0 ? '▼' : '◆'} </span>
                  {formatSentiment(sentimentValues.at(-1))}
                </span>
              </div>
              <p className="trade-chart-context">{company?.industryName ?? 'Industry'} · Latest 48 cycles</p>
              {sentimentHistory === null ? (
                <p className="note note-sm">Loading sentiment history…</p>
              ) : sentimentHistoryFailed ? (
                <p className="note note-sm">Sentiment history is unavailable.</p>
              ) : sentimentValues.length < 2 ? (
                <p className="note note-sm">Not enough sentiment history yet.</p>
              ) : (
                <LineChart
                  values={sentimentValues}
                  tone={toneOf(sentimentChange)}
                  formatValue={formatSentiment}
                  label={`${company?.industryName ?? 'Industry'} sentiment history`}
                />
              )}
            </section>
          </div>

          <dl className="modal-stats">
            <div>
              <dt>Company</dt>
              <dd>{companyName}</dd>
            </div>
            <div>
              <dt>{actor == null ? 'Actor' : 'You'}</dt>
              <dd className={actor == null ? undefined : `tone-${takingSellOffer ? 'up' : 'down'}`}>
                {actor == null ? (
                  'Not selected'
                ) : (
                  <>
                    <span aria-hidden="true">{takingSellOffer ? '▲' : '▼'} </span>
                    {playerSide}
                  </>
                )}
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
            {soldCostBasis != null ? (
              <div>
                <dt>Cost basis</dt>
                <dd className="num">{formatMoney(soldCostBasis)}</dd>
              </div>
            ) : null}
          </dl>

          {availability.eligible && takingSellOffer && actor != null ? (
            <p className="note note-sm">
              {formatMoney(actor.availableBalance)} available cash · {formatMoney(actor.buyingPower)} margin buying power ·{' '}
              {capacity.cashShares} cash / {capacity.marginShares} total shares at this limit
            </p>
          ) : null}

          {!availability.eligible ? (
            <p className="note note-sm" role="status">
              {luld.orderEntryDisabled && availability.reason.startsWith('Order entry')
                ? `${luld.indicator} ${availability.reason} ${luld.executionNote}`
                : availability.reason}
            </p>
          ) : null}

          {!availability.eligible ? null : noBuyingPower ? (
            <p className="note note-sm">This actor has insufficient margin buying power for one share at this limit.</p>
          ) : loadingHoldings ? (
            <p className="note note-sm">Checking your holdings…</p>
          ) : noShares ? (
            <p className="note note-sm">You hold no {companyName} shares to sell.</p>
          ) : (
            <>
              <div className="field">
                <span>Quantity (max {maxQuantity})</span>
                <PercentButtons options={TRADE_QUANTITY_PRESETS} ariaLabel="Set quantity from a percentage" onPick={pickQuantity} />
                <input
                  className="select num"
                  type="number"
                  min="1"
                  max={maxQuantity}
                  step="1"
                  aria-label="Quantity"
                  value={quantity}
                  onChange={(event) => setQuantity(event.target.value)}
                />
              </div>

              <p className="note note-sm">
                Matching runs during a trading cycle at the midpoint price. Any fill settles on the next trading day (T+1).
              </p>
            </>
          )}

          {error ? (
            <p className="command-error" role="alert">
              {error}
            </p>
          ) : null}
          {availability.eligible && !loadingHoldings && !validQuantity && quantity !== '' ? (
            <p className="note note-sm" role="status">{eligibility.reason}</p>
          ) : null}

          <footer className="modal-foot">
            <button type="button" className="btn" ref={closeRef} onClick={onClose}>
              {!availability.eligible || noShares || noBuyingPower ? 'Close' : 'Cancel'}
            </button>
            {!availability.eligible || loadingHoldings || noShares || noBuyingPower ? null : (
              <button type="submit" className="btn btn-primary" disabled={submitting || !validQuantity}>
                {submitting ? 'Placing…' : actor.kind === 'fund' ? 'Place order as managed fund' : 'Place order as player'}
              </button>
            )}
          </footer>
        </form>
      </div>
    </div>
  )
}
