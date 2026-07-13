import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt, formatMoney, formatSigned, toneOf } from './format'
import { Panel } from './Panel'
import { LineChart } from './LineChart'
import { RatingBadge } from './RatingBadge'
import { NewsImpact } from './NewsImpact'
import { NewsModal } from './NewsModal'
import { OrderForm } from './OrderForm'
import { TradeModal } from './TradeModal'
import { Pager } from './TableControls'
import { luldPresentation } from './marketAccounting'

const POLL_INTERVAL_MS = 2500
const PRICE_HISTORY_POINTS = 32
const CORPORATE_CASH_PAGE_SIZE = 10
const RISK_ORDER = { Low: 0, High: 1, Extra: 2 }

function formatPct(fraction) {
  if (typeof fraction !== 'number') return '—'
  const sign = fraction > 0 ? '+' : fraction < 0 ? '−' : ''
  return `${sign}${(Math.abs(fraction) * 100).toFixed(2)}%`
}

// An order/trade quantity as a share of the whole issued float, shown unsigned; sub-0.01% quantities collapse
// to a floor label so a tiny order does not read as exactly 0%.
function formatSharePct(quantity, issuedShares) {
  if (!issuedShares || issuedShares <= 0) return null
  const pct = (quantity / issuedShares) * 100
  if (pct > 0 && pct < 0.01) return '<0.01%'
  return `${pct.toFixed(2)}%`
}

// A price relative to a reference (order limit vs current price, trade price vs the market it hit), signed.
function priceVsReference(price, reference) {
  if (typeof price !== 'number' || typeof reference !== 'number' || reference === 0) return null
  return formatPct((price - reference) / reference)
}

// The direction of the latest rating change, comparing the current verdict's severity to the one before it.
function ratingTrend(current, previous) {
  if (!current || !previous || !(current in RISK_ORDER) || !(previous in RISK_ORDER)) return null
  if (RISK_ORDER[current] > RISK_ORDER[previous]) return 'worsened'
  if (RISK_ORDER[current] < RISK_ORDER[previous]) return 'improved'
  return null
}

// The company detail block: identity, a price-history chart, ownership and shareholders, and recent orders
// and trades. Owns its own polling keyed on companyId so it can sit under the Companies table and swap as the
// selected company changes.
export function CompanyDetail({ companyId }) {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [detail, setDetail] = useState(null)
  const [shareholders, setShareholders] = useState([])
  const [orders, setOrders] = useState([])
  const [trades, setTrades] = useState([])
  const [prices, setPrices] = useState([])
  const [ratings, setRatings] = useState([])
  const [emissions, setEmissions] = useState([])
  const [news, setNews] = useState([])
  const [corporateCashMovements, setCorporateCashMovements] = useState({ items: [], total: 0, page: 1, pageSize: 10 })
  const [corporateCashPage, setCorporateCashPage] = useState(1)
  const [selectedNews, setSelectedNews] = useState(null)
  const [player, setPlayer] = useState(null)
  const [playerOwned, setPlayerOwned] = useState(0)
  const [fundOwned, setFundOwned] = useState(0)

  const loadAll = useCallback(async () => {
    try {
      const [
        detailData,
        shareholderData,
        orderData,
        tradeData,
        priceData,
        ratingData,
        emissionData,
        newsData,
        corporateCashData,
        playerData,
      ] =
        await Promise.all([
          api.getCompany(companyId),
          api.getCompanyShareholders(companyId),
          api.getCompanyOrders(companyId),
          api.getCompanyShareTransactions(companyId),
          api.getPrices(companyId),
          api.getCompanyRatings(companyId),
          api.getCompanyEmissions(companyId),
          api.getCompanyNews(companyId),
          api.getCompanyCorporateCashMovements(companyId, corporateCashPage, CORPORATE_CASH_PAGE_SIZE),
          api.getPlayer(),
        ])

      setDetail(detailData)
      setShareholders(shareholderData)
      setOrders(orderData)
      setTrades(tradeData)
      setPrices(priceData)
      setRatings(ratingData ?? [])
      setEmissions(emissionData ?? [])
      setNews(newsData ?? [])
      setCorporateCashMovements(corporateCashData ?? { items: [], total: 0, page: 1, pageSize: CORPORATE_CASH_PAGE_SIZE })

      setPlayer(playerData)
      if (playerData) {
        const holdings = await api.getHoldings(playerData.id)
        const owned = holdings.find((item) => item.companyId === companyId)
        setPlayerOwned(owned ? owned.shares : 0)
        if (playerData.fundParticipantId != null) {
          const fundHoldings = await api.getHoldings(playerData.fundParticipantId)
          const fundHolding = fundHoldings.find((item) => item.companyId === companyId)
          setFundOwned(fundHolding ? fundHolding.shares : 0)
        } else {
          setFundOwned(0)
        }
      } else {
        setPlayerOwned(0)
        setFundOwned(0)
      }
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [companyId, corporateCashPage])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll])

  if (!ready) {
    return (
      <section className="placeholder" aria-busy="true">
        <span className="spinner" aria-hidden="true" />
        <p>Loading company…</p>
      </section>
    )
  }

  if (detail === null) {
    return (
      <div className="banner" role="alert">
        <strong>Couldn&apos;t load this company.</strong>
        <span>{loadError ?? 'Pick another company from the table.'}</span>
      </div>
    )
  }

  const changeTone = toneOf(detail.priceChangePct)
  const riskTrend = ratingTrend(detail.currentRating, detail.previousRating)
  const luld = luldPresentation(detail.luldState)

  return (
    <section className="detail-stack" aria-label={`${detail.name} details`}>
      {loadError ? (
        <div className="banner" role="alert">
          <strong>Showing last known state.</strong>
          <span>{loadError}</span>
        </div>
      ) : null}

      {detail.isClosed ? (
        <div className="banner" role="status">
          <strong>Delisted.</strong>
          <span>
            This company was delisted
            {typeof detail.closedInCycleNumber === 'number' ? ` in cycle ${detail.closedInCycleNumber}` : ''}. Its
            orders were cancelled and its shares wiped out.
          </span>
        </div>
      ) : null}

      {detail.luldState && detail.luldState !== 'Normal' ? (
        <div className="banner" role="status">
          <strong>{luld.indicator} {luld.label}.</strong>
          <span>
            Reference {formatMoney(detail.referencePrice)} · band {formatMoney(detail.lowerBandPrice)}–
            {formatMoney(detail.upperBandPrice)}
            {detail.luldState === 'TradingPause' ? ` · ${formatInt(detail.remainingPauseSeconds)} trading seconds left` : ''}.
            {' '}{luld.executionNote}
          </span>
        </div>
      ) : null}

      <section className="command" aria-label="Company identity">
        <div className="command-id">
          <span className="command-label">Company</span>
          <h2 className="command-name">{detail.name}</h2>
        </div>
        <dl className="statbar">
          <div className="stat">
            <dt>Industry</dt>
            <dd>{detail.industryName ?? '—'}</dd>
          </div>
          <div className="stat">
            <dt>Price</dt>
            <dd className="num">{formatMoney(detail.currentPrice)}</dd>
          </div>
          <div className="stat">
            <dt>Change</dt>
            <dd className={`num tone-${changeTone}`}>
              <span aria-hidden="true">{changeTone === 'up' ? '▲ ' : changeTone === 'down' ? '▼ ' : ''}</span>
              {formatPct(detail.priceChangePct)}
            </dd>
          </div>
          <div className="stat">
            <dt>Issued shares</dt>
            <dd className="num">{formatInt(detail.issuedSharesCount)}</dd>
          </div>
          <div className="stat">
            <dt>Market cap</dt>
            <dd className="num">{formatMoney(detail.marketCap)}</dd>
          </div>
          <div className="stat">
            <dt>Issuer cash</dt>
            <dd className="num">{formatMoney(detail.issuerCash)}</dd>
          </div>
          <div className="stat">
            <dt>Price control</dt>
            <dd className={`tone-${luld.tone}`}>
              <span aria-hidden="true">{luld.indicator} </span>{luld.label}
            </dd>
          </div>
          <div className="stat">
            <dt>Price band</dt>
            <dd className="num">
              {detail.lowerBandPrice != null && detail.upperBandPrice != null
                ? `${formatMoney(detail.lowerBandPrice)}–${formatMoney(detail.upperBandPrice)}`
                : '—'}
            </dd>
          </div>
          <div className="stat">
            <dt>Risk rating</dt>
            <dd>
              {detail.currentRating ? (
                <span className="rating-stat">
                  <RatingBadge rating={detail.currentRating} />
                  {riskTrend ? (
                    <span className="rating-trend">
                      {riskTrend === 'worsened' ? '▲' : '▼'} {riskTrend}
                    </span>
                  ) : null}
                </span>
              ) : (
                '—'
              )}
            </dd>
          </div>
        </dl>
      </section>

      <PriceChartPanel name={detail.name} prices={prices} />

      <CorporateCashMovementsPanel
        movements={corporateCashMovements}
        page={corporateCashPage}
        onPage={setCorporateCashPage}
      />

      {player && !detail.isClosed ? (
        <TradePanel
          companyId={companyId}
          currentPrice={detail.currentPrice}
          player={player}
          playerOwned={playerOwned}
          fundOwned={fundOwned}
          luldState={detail.luldState}
          lowerBandPrice={detail.lowerBandPrice}
          upperBandPrice={detail.upperBandPrice}
          onPlaced={loadAll}
        />
      ) : null}

      <div className="grid-detail">
        <OwnershipPanel detail={detail} />
        <ShareholdersPanel shareholders={shareholders} />
      </div>

      <OrdersPanel orders={orders} currentPrice={detail.currentPrice} issuedShares={detail.issuedSharesCount} />
      <TradesPanel trades={trades} companyName={detail.name} />

      <div className="grid-detail">
        <RatingHistoryPanel ratings={ratings} />
        <EmissionsPanel emissions={emissions} />
      </div>

      <RelatedNewsPanel news={news} onSelect={setSelectedNews} />

      {selectedNews ? <NewsModal post={selectedNews} onClose={() => setSelectedNews(null)} /> : null}
    </section>
  )
}

function CorporateCashMovementsPanel({ movements, page, onPage }) {
  const items = movements.items ?? []
  const pageCount = Math.max(1, Math.ceil((movements.total ?? 0) / (movements.pageSize || CORPORATE_CASH_PAGE_SIZE)))

  return (
    <Panel title="Corporate cash movements" count={`${formatInt(movements.total ?? 0)} total`} className="panel-cash">
      {items.length === 0 ? (
        <p className="note">No issuer cash movements yet.</p>
      ) : (
        <>
          <div className="tbl-wrap">
            <table className="tbl">
              <thead>
                <tr>
                  <th scope="col">Type</th>
                  <th scope="col" className="ta-r">
                    Amount
                  </th>
                  <th scope="col" className="ta-r">
                    Cycle
                  </th>
                </tr>
              </thead>
              <tbody>
                {items.map((movement) => {
                  const credit = movement.type === 'PrimaryIssuance'
                  const label =
                    movement.type === 'PrimaryIssuance'
                      ? 'Primary issuance'
                      : movement.type === 'DividendDeclared'
                        ? 'Dividend paid'
                        : 'Closure distribution'
                  return (
                    <tr key={movement.id}>
                      <th scope="row">{label}</th>
                      <td className={`num ta-r tone-${credit ? 'up' : 'down'}`}>
                        <span aria-label={credit ? 'Credit' : 'Debit'}>{credit ? '+ ' : '− '}</span>
                        {formatMoney(movement.amount)}
                      </td>
                      <td className="num ta-r">{formatInt(movement.createdInCycleNumber || movement.createdInCycleId)}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          <Pager page={page} pageCount={pageCount} onPage={onPage} />
        </>
      )}
    </Panel>
  )
}

// Trade the company as the player or, if the player runs a fund, through the fund. Buy/Sell reveal the shared
// order form; a placed order refreshes the page so the new order and balances show at once.
function TradePanel({ companyId, currentPrice, player, playerOwned, fundOwned, luldState, lowerBandPrice, upperBandPrice, onPlaced }) {
  const [side, setSide] = useState('none')
  const fund =
    player.fundParticipantId != null
      ? { id: player.fundParticipantId, name: player.fundName, availableBalance: player.fundAvailableBalance, margin: player.fundMargin }
      : null
  const canSell = playerOwned > 0 || fundOwned > 0
  const company = { id: companyId, currentPrice, luldState, lowerBandPrice, upperBandPrice }

  return (
    <Panel title="Trade" className="panel-orders-list">
      <div className="order-actions">
        <button
          type="button"
          className="btn btn-primary"
          aria-expanded={side === 'buy'}
          onClick={() => setSide((current) => (current === 'buy' ? 'none' : 'buy'))}
        >
          Buy shares
        </button>
        {canSell ? (
          <button
            type="button"
            className="btn"
            aria-expanded={side === 'sell'}
            onClick={() => setSide((current) => (current === 'sell' ? 'none' : 'sell'))}
          >
            Sell shares
          </button>
        ) : null}
      </div>
      {side === 'buy' ? (
        <OrderForm key={`buy-${companyId}`} player={player} fund={fund} company={company} side="Buy" onPlaced={onPlaced} />
      ) : null}
      {side === 'sell' ? (
        <OrderForm
          key={`sell-${companyId}`}
          player={player}
          fund={fund}
          company={company}
          side="Sell"
          playerMaxQuantity={playerOwned}
          fundMaxQuantity={fundOwned}
          onPlaced={onPlaced}
        />
      ) : null}
    </Panel>
  )
}

function RelatedNewsPanel({ news, onSelect }) {
  return (
    <Panel title="Related news" count={`${news.length}`} className="panel-orders-list">
      {news.length === 0 ? (
        <p className="note">No news for this company or its industry yet.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col" className="ta-r">
                  Impact
                </th>
              </tr>
            </thead>
            <tbody>
              {news.map((post) => (
                <tr key={post.id}>
                  <th scope="row">
                    <button
                      type="button"
                      className="cell-name-btn"
                      onClick={() => onSelect(post)}
                      title={`Open ${post.title}`}
                    >
                      {post.title}
                    </button>
                  </th>
                  <td className="ta-r">
                    <NewsImpact post={post} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function RatingHistoryPanel({ ratings }) {
  return (
    <Panel title="Risk ratings" count={`last ${ratings.length}`} className="panel-orders-list">
      {ratings.length === 0 ? (
        <p className="note">No auditor has reviewed this company yet.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Result</th>
                <th scope="col">Auditor</th>
                <th scope="col" className="ta-r">
                  Cycles ago
                </th>
              </tr>
            </thead>
            <tbody>
              {ratings.map((rating) => (
                <tr key={rating.id}>
                  <td>
                    <RatingBadge rating={rating.rating} impactPercent={rating.impactPercent} />
                  </td>
                  <td className="cell-ellipsis">{rating.auditorName}</td>
                  <td className="num ta-r">{formatInt(rating.cyclesAgo)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function EmissionsPanel({ emissions }) {
  return (
    <Panel title="Share emissions" count={`${emissions.length}`} className="panel-trades">
      {emissions.length === 0 ? (
        <p className="note">No free-share emissions yet.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col" className="ta-r">
                  Shares
                </th>
                <th scope="col" className="ta-r">
                  Recipients
                </th>
                <th scope="col" className="ta-r">
                  Cycles ago
                </th>
              </tr>
            </thead>
            <tbody>
              {emissions.map((emission) => (
                <tr key={emission.id}>
                  <td className="num ta-r">{formatInt(emission.sharesEmitted)}</td>
                  <td className="num ta-r">{formatInt(emission.recipientCount)}</td>
                  <td className="num ta-r">{formatInt(emission.cyclesAgo)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function PriceChartPanel({ name, prices }) {
  const values = prices.map((snapshot) => snapshot.price)
  const last = values.at(-1)
  const first = values.at(0)
  const low = values.length ? Math.min(...values) : undefined
  const high = values.length ? Math.max(...values) : undefined
  const change = values.length >= 2 ? last - first : 0
  const changePct = first ? (change / first) * 100 : 0
  const tone = toneOf(change)

  return (
    <Panel
      title={`Price · ${name}`}
      count={`${prices.length} snapshot${prices.length === 1 ? '' : 's'}`}
      className="panel-chart"
    >
      {values.length < 2 ? (
        <p className="note">Not enough price history yet. Start the loop or step a cycle to record trades.</p>
      ) : (
        <>
          <div className="quote">
            <strong className="quote-last num">{formatMoney(last)}</strong>
            <span className={`quote-change num tone-${tone}`}>
              <span aria-hidden="true">{tone === 'up' ? '▲' : tone === 'down' ? '▼' : '◆'}</span>
              {formatSigned(change)}
              <span className="quote-pct">
                ({change > 0 ? '+' : change < 0 ? '−' : ''}
                {Math.abs(changePct).toFixed(2)}%)
              </span>
            </span>
          </div>
          <LineChart values={values.slice(-PRICE_HISTORY_POINTS)} tone={tone} />
          <dl className="quote-meta">
            <div>
              <dt>Open</dt>
              <dd className="num">{formatMoney(first)}</dd>
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
        </>
      )}
    </Panel>
  )
}

function OwnershipPanel({ detail }) {
  const rows = [
    { label: 'Issued shares', value: formatInt(detail.issuedSharesCount) },
    { label: 'Held by issuer', value: formatInt(detail.sharesHeldByIssuer) },
    { label: 'Outstanding', value: formatInt(detail.sharesOutstanding) },
    { label: 'Shareholders', value: formatInt(detail.shareholderCount) },
  ]
  const floatPct = detail.issuedSharesCount > 0 ? detail.sharesOutstanding / detail.issuedSharesCount : 0

  return (
    <Panel title="Ownership" count="Float" className="panel-bank">
      <dl className="kv">
        {rows.map((row) => (
          <div className="kv-row" key={row.label}>
            <dt>{row.label}</dt>
            <dd className="num">{row.value}</dd>
          </div>
        ))}
        <div className="kv-row kv-total">
          <dt>Float in market</dt>
          <dd className="num">{formatPct(floatPct)}</dd>
        </div>
      </dl>
    </Panel>
  )
}

function ShareholdersPanel({ shareholders }) {
  return (
    <Panel title="Shareholders" count={`${shareholders.length}`} className="panel-holdings">
      {shareholders.length === 0 ? (
        <p className="note">No participant owns shares yet.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Owner</th>
                <th scope="col" className="ta-r">
                  Shares
                </th>
                <th scope="col" className="ta-r">
                  % issued
                </th>
                <th scope="col" className="ta-r">
                  Value
                </th>
              </tr>
            </thead>
            <tbody>
              {shareholders.map((holder) => (
                <tr key={holder.ownerId}>
                  <th scope="row" className="cell-ellipsis">
                    <Link
                      className="cell-link"
                      to={`/traders/${holder.ownerId}`}
                      title={`Open ${holder.ownerName} trader page`}
                    >
                      {holder.ownerName}
                    </Link>
                  </th>
                  <td className="num ta-r">{formatInt(holder.shares)}</td>
                  <td className="num ta-r">{formatPct(holder.pctOfIssued)}</td>
                  <td className="num ta-r">{formatMoney(holder.marketValue)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function OrdersPanel({ orders, currentPrice, issuedShares }) {
  return (
    <Panel title="Recent orders" count={`last ${orders.length}`} className="panel-orders-list">
      {orders.length === 0 ? (
        <p className="note">No orders placed yet.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Side</th>
                <th scope="col" className="ta-r">
                  Qty
                </th>
                <th scope="col" className="ta-r">
                  Limit
                </th>
                <th scope="col" className="ta-r">
                  Market price
                </th>
                <th scope="col">Status</th>
                <th scope="col">Order owner</th>
              </tr>
            </thead>
            <tbody>
              {orders.map((order) => {
                const sharePct = formatSharePct(order.quantity, issuedShares)
                const vsMarket = priceVsReference(order.limitPrice, currentPrice)
                return (
                  <tr key={order.id}>
                    <td className={`tone-${order.type === 'Buy' ? 'up' : 'down'}`}>{order.type}</td>
                    <td className="num ta-r">
                      {formatInt(order.filledQuantity)}
                      <span className="muted-sub">/{formatInt(order.quantity)}</span>
                      {sharePct ? <span className="muted-sub"> · {sharePct}</span> : null}
                    </td>
                    <td className="num ta-r">
                      {formatMoney(order.limitPrice)}
                      {vsMarket ? <span className="muted-sub"> {vsMarket}</span> : null}
                    </td>
                    <td className="num ta-r">{formatMoney(currentPrice)}</td>
                    <td>{order.status}</td>
                    <td className="cell-ellipsis">
                      {order.participantId != null ? (
                        <Link
                          className="cell-link"
                          to={`/traders/${order.participantId}`}
                          title={`Open ${order.participantName ?? 'trader'} trader page`}
                        >
                          {order.participantName ?? `#${order.participantId}`}
                        </Link>
                      ) : (
                        <span className="muted-sub">Issuer</span>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function TradesPanel({ trades, companyName }) {
  const [selectedTrade, setSelectedTrade] = useState(null)

  // The buyer/seller cells carry their own trader links, so those stop propagation to keep navigating instead
  // of opening the trade dialog.
  const stopRow = { onClick: (event) => event.stopPropagation(), onKeyDown: (event) => event.stopPropagation() }

  return (
    <Panel title="Recent trades" count={`last ${trades.length}`} className="panel-trades">
      {trades.length === 0 ? (
        <p className="note">No settled trades yet.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col" className="ta-r">
                  Qty
                </th>
                <th scope="col" className="ta-r">
                  Price
                </th>
                <th scope="col" className="ta-r">
                  Market price
                </th>
                <th scope="col" className="ta-r">
                  Total
                </th>
                <th scope="col" className="ta-r">
                  Cycle
                </th>
                <th scope="col">Buyer</th>
                <th scope="col">Seller</th>
              </tr>
            </thead>
            <tbody>
              {trades.map((trade) => {
                const vsMarket = priceVsReference(trade.price, trade.marketPriceBefore)
                return (
                  <tr
                    key={trade.id}
                    className="tbl-row-click"
                    role="button"
                    tabIndex={0}
                    aria-label={`Open details for the trade of ${formatInt(trade.quantity)} shares at ${formatMoney(trade.price)}`}
                    onClick={() => setSelectedTrade(trade)}
                    onKeyDown={(event) => {
                      if (event.key === 'Enter' || event.key === ' ') {
                        event.preventDefault()
                        setSelectedTrade(trade)
                      }
                    }}
                  >
                    <td className="num ta-r">{formatInt(trade.quantity)}</td>
                    <td className="num ta-r">
                      {formatMoney(trade.price)}
                      {vsMarket ? <span className="muted-sub"> {vsMarket}</span> : null}
                    </td>
                    <td className="num ta-r">
                      {trade.marketPriceBefore != null ? formatMoney(trade.marketPriceBefore) : '—'}
                    </td>
                    <td className="num ta-r">{formatMoney(trade.totalCost)}</td>
                    <td className="num ta-r">#{trade.createdInCycleId}</td>
                    <td className="cell-ellipsis">
                      <Link
                        className="cell-link"
                        to={`/traders/${trade.buyerId}`}
                        title={`Open ${trade.buyerName ?? 'buyer'} trader page`}
                        {...stopRow}
                      >
                        {trade.buyerName ?? `#${trade.buyerId}`}
                      </Link>
                    </td>
                    <td className="cell-ellipsis">
                      {trade.sellerId != null ? (
                        <Link
                          className="cell-link"
                          to={`/traders/${trade.sellerId}`}
                          title={`Open ${trade.sellerName ?? 'seller'} trader page`}
                          {...stopRow}
                        >
                          {trade.sellerName ?? `#${trade.sellerId}`}
                        </Link>
                      ) : (
                        <span className="muted-sub">Issuer</span>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
      {selectedTrade ? (
        <TradeModal
          trade={selectedTrade}
          companyName={companyName}
          onClose={() => setSelectedTrade(null)}
        />
      ) : null}
    </Panel>
  )
}
