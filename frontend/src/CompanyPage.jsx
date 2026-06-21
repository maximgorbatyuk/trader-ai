import { useCallback, useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt, formatMoney, formatSigned, toneOf } from './format'
import { Panel } from './Panel'
import { LineChart } from './LineChart'

const POLL_INTERVAL_MS = 2500

function formatPct(fraction) {
  if (typeof fraction !== 'number') return '—'
  const sign = fraction > 0 ? '+' : fraction < 0 ? '−' : ''
  return `${sign}${(Math.abs(fraction) * 100).toFixed(2)}%`
}

function CompanyPage() {
  const { id } = useParams()
  const companyId = Number(id)

  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [detail, setDetail] = useState(null)
  const [shareholders, setShareholders] = useState([])
  const [orders, setOrders] = useState([])
  const [trades, setTrades] = useState([])
  const [prices, setPrices] = useState([])

  const loadAll = useCallback(async () => {
    try {
      const [detailData, shareholderData, orderData, tradeData, priceData] = await Promise.all([
        api.getCompany(companyId),
        api.getCompanyShareholders(companyId),
        api.getCompanyOrders(companyId),
        api.getCompanyShareTransactions(companyId),
        api.getPrices(companyId),
      ])

      setDetail(detailData)
      setShareholders(shareholderData)
      setOrders(orderData)
      setTrades(tradeData)
      setPrices(priceData)
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

  const changeTone = toneOf(detail?.priceChangePct)

  return (
    <div className="app">
      <header className="topbar">
        <Link className="brand" to="/" aria-label="Back to the Trader AI dashboard">
          <span className="brand-mark" aria-hidden="true">
            TA
          </span>
          <span className="brand-name">Trader&nbsp;AI</span>
          <span className="brand-tag" aria-hidden="true">
            Company
          </span>
        </Link>
        <Link className="btn" to="/">
          ← Dashboard
        </Link>
      </header>

      <main className="main participant-page">
        {!ready ? (
          <section className="placeholder" aria-busy="true">
            <span className="spinner" aria-hidden="true" />
            <p>Loading company…</p>
          </section>
        ) : detail === null ? (
          <div className="banner" role="alert">
            <strong>Couldn&apos;t load this company.</strong>
            <span>{loadError ?? 'Try again from the dashboard.'}</span>
          </div>
        ) : (
          <>
            {loadError ? (
              <div className="banner" role="alert">
                <strong>Showing last known state.</strong>
                <span>{loadError}</span>
              </div>
            ) : null}

            <section className="command" aria-label="Company identity">
              <div className="command-id">
                <span className="command-label">Company</span>
                <h1 className="command-name">{detail.name}</h1>
              </div>
              <dl className="statbar">
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
          </>
        )}
      </main>
    </div>
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
          <LineChart values={values.slice(-32)} tone={tone} />
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
                    <a
                      className="cell-link"
                      href={`/participants/${holder.ownerId}`}
                      target="_blank"
                      rel="noopener"
                      title={`Open ${holder.ownerName} detail page in a new tab`}
                    >
                      {holder.ownerName}
                      <span aria-hidden="true"> ↗</span>
                    </a>
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

export default CompanyPage
