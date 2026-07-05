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

const POLL_INTERVAL_MS = 2500
const PRICE_HISTORY_POINTS = 32
const RISK_ORDER = { Low: 0, High: 1, Extra: 2 }

function formatPct(fraction) {
  if (typeof fraction !== 'number') return '—'
  const sign = fraction > 0 ? '+' : fraction < 0 ? '−' : ''
  return `${sign}${(Math.abs(fraction) * 100).toFixed(2)}%`
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
  const [selectedNews, setSelectedNews] = useState(null)

  const loadAll = useCallback(async () => {
    try {
      const [detailData, shareholderData, orderData, tradeData, priceData, ratingData, emissionData, newsData] =
        await Promise.all([
          api.getCompany(companyId),
          api.getCompanyShareholders(companyId),
          api.getCompanyOrders(companyId),
          api.getCompanyShareTransactions(companyId),
          api.getPrices(companyId),
          api.getCompanyRatings(companyId),
          api.getCompanyEmissions(companyId),
          api.getCompanyNews(companyId),
        ])

      setDetail(detailData)
      setShareholders(shareholderData)
      setOrders(orderData)
      setTrades(tradeData)
      setPrices(priceData)
      setRatings(ratingData ?? [])
      setEmissions(emissionData ?? [])
      setNews(newsData ?? [])
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [companyId])

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

  return (
    <section className="detail-stack" aria-label={`${detail.name} details`}>
      {loadError ? (
        <div className="banner" role="alert">
          <strong>Showing last known state.</strong>
          <span>{loadError}</span>
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

      <div className="grid-detail">
        <OwnershipPanel detail={detail} />
        <ShareholdersPanel shareholders={shareholders} />
      </div>

      <div className="grid-detail">
        <OrdersPanel orders={orders} />
        <TradesPanel trades={trades} />
      </div>

      <div className="grid-detail">
        <RatingHistoryPanel ratings={ratings} />
        <EmissionsPanel emissions={emissions} />
      </div>

      <RelatedNewsPanel news={news} onSelect={setSelectedNews} />

      {selectedNews ? <NewsModal post={selectedNews} onClose={() => setSelectedNews(null)} /> : null}
    </section>
  )
}

function RelatedNewsPanel({ news, onSelect }) {
  return (
    <Panel title="Related news" count={`${news.length}`} className="panel-orders-list">
      {news.length === 0 ? (
        <p className="note">No news for this company or its industry yet.</p>
      ) : (
        <div className="tbl-scroll">
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
        <div className="tbl-scroll">
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
        <div className="tbl-scroll">
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
        <div className="tbl-scroll">
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

function OrdersPanel({ orders }) {
  return (
    <Panel title="Recent orders" count={`last ${orders.length}`} className="panel-orders-list">
      {orders.length === 0 ? (
        <p className="note">No orders placed yet.</p>
      ) : (
        <div className="tbl-scroll">
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
                <th scope="col">Status</th>
              </tr>
            </thead>
            <tbody>
              {orders.map((order) => (
                <tr key={order.id}>
                  <td className={`tone-${order.type === 'Buy' ? 'up' : 'down'}`}>{order.type}</td>
                  <td className="num ta-r">
                    {order.filledQuantity}
                    <span className="muted-sub">/{order.quantity}</span>
                  </td>
                  <td className="num ta-r">{formatMoney(order.limitPrice)}</td>
                  <td>{order.status}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function TradesPanel({ trades }) {
  return (
    <Panel title="Recent trades" count={`last ${trades.length}`} className="panel-trades">
      {trades.length === 0 ? (
        <p className="note">No settled trades yet.</p>
      ) : (
        <div className="tbl-scroll">
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
                  Total
                </th>
                <th scope="col" className="ta-r">
                  Cycle
                </th>
              </tr>
            </thead>
            <tbody>
              {trades.map((trade) => (
                <tr key={trade.id}>
                  <td className="num ta-r">{formatInt(trade.quantity)}</td>
                  <td className="num ta-r">{formatMoney(trade.price)}</td>
                  <td className="num ta-r">{formatMoney(trade.totalCost)}</td>
                  <td className="num ta-r">#{trade.createdInCycleId}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}
