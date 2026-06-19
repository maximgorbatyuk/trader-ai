import { useCallback, useEffect, useRef, useState } from 'react'
import './App.css'
import { api } from './api'

const POLL_INTERVAL_MS = 2500
const OPEN_STATUSES = new Set(['Open', 'PartiallyFilled'])

const moneyFormatter = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
})
const intFormatter = new Intl.NumberFormat('en-US')

function formatMoney(value) {
  return typeof value === 'number' ? moneyFormatter.format(value) : '—'
}

function formatInt(value) {
  return typeof value === 'number' ? intFormatter.format(value) : '—'
}

function formatSigned(value) {
  if (typeof value !== 'number') return '—'
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  return `${sign}${moneyFormatter.format(Math.abs(value))}`
}

function toneOf(value) {
  if (typeof value !== 'number' || value === 0) return 'flat'
  return value > 0 ? 'up' : 'down'
}

// A null participant is the share issuer's own offering (seeded company sell orders).
function traderName(id, byId) {
  if (id == null) return 'Issuer'
  return byId.get(id) ?? `#${id}`
}

// Briefly tints a readout when its value changes, so a moving market is felt, not just read.
function useChangeFlash(value) {
  const previous = useRef(value)
  const [flash, setFlash] = useState('')

  useEffect(() => {
    const prev = previous.current
    previous.current = value
    if (prev == null || value == null || value === prev) return undefined
    setFlash(value > prev ? 'flash-up' : 'flash-down')
    const timer = setTimeout(() => setFlash(''), 720)
    return () => clearTimeout(timer)
  }, [value])

  return flash
}

function App() {
  const [ready, setReady] = useState(false)
  const [connected, setConnected] = useState(false)
  const [market, setMarket] = useState(null)
  const [companies, setCompanies] = useState([])
  const [participants, setParticipants] = useState([])
  const [orders, setOrders] = useState([])
  const [cycles, setCycles] = useState([])
  const [transactions, setTransactions] = useState([])
  const [prices, setPrices] = useState([])
  const [selectedCompanyId, setSelectedCompanyId] = useState(null)
  const [pending, setPending] = useState(false)
  const [actionError, setActionError] = useState(null)

  const selectedRef = useRef(null)
  useEffect(() => {
    selectedRef.current = selectedCompanyId
  }, [selectedCompanyId])

  const loadAll = useCallback(async () => {
    try {
      const [marketData, companyData, participantData, orderData, cycleData, transactionData] = await Promise.all([
        api.getMarket(),
        api.getCompanies(),
        api.getParticipants(),
        api.getOrders('open'),
        api.getCycles(),
        api.getShareTransactions(50),
      ])

      setMarket(marketData)
      setCompanies(companyData)
      setParticipants(participantData)
      setOrders(orderData)
      setCycles(cycleData)
      setTransactions(transactionData)
      setConnected(true)

      let companyId = selectedRef.current
      if (!companyId && companyData.length > 0) {
        companyId = companyData[0].id
        setSelectedCompanyId(companyId)
      }

      setPrices(companyId ? await api.getPrices(companyId) : [])
    } catch {
      setConnected(false)
    } finally {
      setReady(true)
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

  useEffect(() => {
    if (!selectedCompanyId) {
      return
    }

    api.getPrices(selectedCompanyId).then(setPrices).catch(() => {})
  }, [selectedCompanyId])

  async function runAction(action) {
    setPending(true)
    setActionError(null)
    try {
      await action()
      await loadAll()
    } catch (error) {
      setActionError(error.message)
    } finally {
      setPending(false)
    }
  }

  const participantNameById = new Map(participants.map((participant) => [participant.id, participant.name]))
  const companyNameById = new Map(companies.map((company) => [company.id, company.name]))
  const openOrders = orders.filter((order) => OPEN_STATUSES.has(order.status))
  const currentCycleNumber =
    cycles.find((cycle) => cycle.id === market?.currentCycleId)?.cycleNumber ?? cycles.length

  return (
    <div className="app">
      <TopBar connected={connected} ready={ready} market={market} />

      <main className="main">
        {!ready ? (
          <ConnectingState />
        ) : (
          <>
            {!connected ? <OfflineBanner /> : null}

            {market === null && connected ? (
              <SeedPanel pending={pending} runAction={runAction} />
            ) : null}

            {market !== null ? (
              <>
                <CommandStrip
                  market={market}
                  currentCycleNumber={currentCycleNumber}
                  openOrders={openOrders.length}
                  trades={transactions.length}
                  participants={participants.length}
                  companies={companies.length}
                  pending={pending}
                  actionError={actionError}
                  runAction={runAction}
                />

                <div className="grid">
                  <PriceChartPanel
                    companies={companies}
                    selectedCompanyId={selectedCompanyId}
                    onSelect={setSelectedCompanyId}
                    prices={prices}
                  />
                  <WatchlistPanel
                    companies={companies}
                    selectedCompanyId={selectedCompanyId}
                    onSelect={setSelectedCompanyId}
                  />
                  <OrderBookPanel
                    orders={openOrders}
                    participantNameById={participantNameById}
                    companyNameById={companyNameById}
                  />
                  <ParticipantsPanel participants={participants} />
                  <PlaceOrderPanel
                    participants={participants}
                    companies={companies}
                    pending={pending}
                    runAction={runAction}
                  />
                  <TradeTapePanel
                    transactions={transactions}
                    participantNameById={participantNameById}
                    companyNameById={companyNameById}
                  />
                </div>
              </>
            ) : null}
          </>
        )}
      </main>
    </div>
  )
}

function TopBar({ connected, ready, market }) {
  return (
    <header className="topbar">
      <a className="brand" href="/" aria-label="Trader AI dashboard">
        <span className="brand-mark" aria-hidden="true">
          TA
        </span>
        <span className="brand-name">Trader&nbsp;AI</span>
        <span className="brand-tag" aria-hidden="true">
          Market Simulator
        </span>
      </a>
      <div className="topbar-status">
        {market ? <StatusBadge status={market.status} /> : null}
        <ConnPill connected={connected} ready={ready} />
      </div>
    </header>
  )
}

function ConnPill({ connected, ready }) {
  const state = !ready ? 'pending' : connected ? 'live' : 'down'
  const label = !ready ? 'Connecting' : connected ? 'Backend live' : 'Backend offline'

  return (
    <span className={`conn conn-${state}`} role="status">
      <span className="conn-dot" aria-hidden="true" />
      {label}
    </span>
  )
}

const STATUS_TONE = {
  Running: 'up',
  Paused: 'attention',
  Completed: 'muted',
  NotStarted: 'muted',
}

function StatusBadge({ status }) {
  const tone = STATUS_TONE[status] ?? 'muted'
  return <span className={`pill pill-${tone}`}>{status}</span>
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

function CommandStrip({
  market,
  currentCycleNumber,
  openOrders,
  trades,
  participants,
  companies,
  pending,
  actionError,
  runAction,
}) {
  const running = market.status === 'Running'

  const stats = [
    { label: 'Cycle', value: currentCycleNumber > 0 ? `#${currentCycleNumber}` : '—' },
    { label: 'Open orders', value: formatInt(openOrders) },
    { label: 'Trades', value: formatInt(trades) },
    { label: 'Companies', value: formatInt(companies) },
    { label: 'Traders', value: formatInt(participants) },
  ]

  return (
    <section className="command" aria-label="Market status and controls">
      <div className="command-id">
        <span className="command-label">Market</span>
        <h1 className="command-name">{market.name}</h1>
      </div>

      <dl className="statbar">
        {stats.map((stat) => (
          <div className="stat" key={stat.label}>
            <dt>{stat.label}</dt>
            <dd className="num">{stat.value}</dd>
          </div>
        ))}
      </dl>

      <div className="controls">
        <button className="btn" disabled={pending} onClick={() => runAction(api.runDecisions)}>
          Run decisions
        </button>
        <button className="btn" disabled={pending} onClick={() => runAction(api.advanceCycle)}>
          Advance cycle
        </button>
        {running ? (
          <button className="btn btn-primary" disabled={pending} onClick={() => runAction(api.pauseMarket)}>
            Pause loop
          </button>
        ) : (
          <button className="btn btn-primary" disabled={pending} onClick={() => runAction(api.startMarket)}>
            Start loop
          </button>
        )}
      </div>

      {actionError ? (
        <p className="command-error" role="alert">
          {actionError}
        </p>
      ) : null}
    </section>
  )
}

function Panel({ title, count, className = '', headerExtra, children }) {
  return (
    <article className={`panel ${className}`}>
      <div className="panel-head">
        <h2>{title}</h2>
        <div className="panel-head-meta">
          {typeof count === 'string' ? <span className="panel-count">{count}</span> : null}
          {headerExtra}
        </div>
      </div>
      {children}
    </article>
  )
}

function PriceChartPanel({ companies, selectedCompanyId, onSelect, prices }) {
  const values = prices.map((snapshot) => snapshot.price)
  const last = values.at(-1)
  const first = values.at(0)
  const low = values.length ? Math.min(...values) : undefined
  const high = values.length ? Math.max(...values) : undefined
  const change = values.length >= 2 ? last - first : 0
  const changePct = first ? (change / first) * 100 : 0
  const tone = toneOf(change)
  const flash = useChangeFlash(last)

  const select = (
    <select
      className="select select-sm"
      aria-label="Select company"
      value={selectedCompanyId ?? ''}
      onChange={(event) => onSelect(Number(event.target.value))}
    >
      {companies.map((company) => (
        <option key={company.id} value={company.id}>
          {company.name}
        </option>
      ))}
    </select>
  )

  return (
    <Panel
      title="Price"
      count={`${prices.length} snapshot${prices.length === 1 ? '' : 's'}`}
      className="panel-chart"
      headerExtra={select}
    >
      {values.length < 2 ? (
        <p className="note">Not enough price history yet. Advance a cycle to record trades.</p>
      ) : (
        <>
          <div className="quote">
            <strong className={`quote-last num ${flash}`}>{formatMoney(last)}</strong>
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

function LineChart({ values, tone }) {
  const width = 720
  const height = 220
  const padX = 6
  const padY = 16
  const min = Math.min(...values)
  const max = Math.max(...values)
  const range = max - min
  const plot = height - padY * 2

  const points = values.map((value, index) => ({
    x: padX + (index * (width - padX * 2)) / (values.length - 1),
    // A flat (zero-range) series centers vertically instead of pinning to the floor.
    y: range === 0 ? height / 2 : padY + plot - ((value - min) / range) * plot,
  }))

  const line = points.map((point) => `${point.x},${point.y}`).join(' ')
  const area = `${padX},${height} ${line} ${width - padX},${height}`
  const last = points.at(-1)

  return (
    <div className={`chart tone-${tone}`}>
      <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label="Price history line chart">
        <defs>
          <linearGradient id="chart-fill" x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor="currentColor" stopOpacity="0.16" />
            <stop offset="100%" stopColor="currentColor" stopOpacity="0" />
          </linearGradient>
        </defs>
        <g className="chart-grid" aria-hidden="true">
          {[0.25, 0.5, 0.75].map((fraction) => (
            <line key={fraction} x1={padX} x2={width - padX} y1={padY + fraction * (height - padY * 2)} y2={padY + fraction * (height - padY * 2)} />
          ))}
        </g>
        <polygon points={area} fill="url(#chart-fill)" />
        <polyline className="chart-line" points={line} />
        {last ? <circle className="chart-dot" cx={last.x} cy={last.y} r="3.5" /> : null}
      </svg>
    </div>
  )
}

function WatchlistPanel({ companies, selectedCompanyId, onSelect }) {
  return (
    <Panel title="Companies" count={`${companies.length}`} className="panel-watchlist">
      {companies.length === 0 ? (
        <p className="note">No companies yet.</p>
      ) : (
        <ul className="watchlist">
          {companies.map((company) => {
            const active = company.id === selectedCompanyId
            return (
              <li key={company.id}>
                <button
                  type="button"
                  className={`watch-row ${active ? 'is-active' : ''}`}
                  aria-pressed={active}
                  onClick={() => onSelect(company.id)}
                >
                  <span className="watch-name">{company.name}</span>
                  <span className="watch-price num">{formatMoney(company.currentPrice)}</span>
                </button>
              </li>
            )
          })}
        </ul>
      )}
    </Panel>
  )
}

function OrderBookPanel({ orders, participantNameById, companyNameById }) {
  const bids = orders
    .filter((order) => order.type === 'Buy')
    .sort((a, b) => b.limitPrice - a.limitPrice)
  const asks = orders
    .filter((order) => order.type === 'Sell')
    .sort((a, b) => a.limitPrice - b.limitPrice)

  return (
    <Panel title="Order book" count={`${orders.length} open`} className="panel-book">
      <div className="book">
        <OrderSide
          side="Bids"
          tone="up"
          orders={bids}
          participantNameById={participantNameById}
          companyNameById={companyNameById}
        />
        <OrderSide
          side="Asks"
          tone="down"
          orders={asks}
          participantNameById={participantNameById}
          companyNameById={companyNameById}
        />
      </div>
    </Panel>
  )
}

function OrderSide({ side, tone, orders, participantNameById, companyNameById }) {
  return (
    <div className="book-side">
      <div className={`book-side-head tone-${tone}`}>
        <span>{side}</span>
        <span className="num">{orders.length}</span>
      </div>
      {orders.length === 0 ? (
        <p className="note note-sm">No {side.toLowerCase()}.</p>
      ) : (
        <table className="tbl tbl-book">
          <thead>
            <tr>
              <th scope="col">Price</th>
              <th scope="col" className="ta-r">
                Qty
              </th>
              <th scope="col">Trader</th>
            </tr>
          </thead>
          <tbody>
            {orders.map((order) => (
              <tr key={order.id}>
                <td className={`num tone-${tone}`}>{formatMoney(order.limitPrice)}</td>
                <td className="num ta-r">
                  {order.quantity - order.filledQuantity}
                  <span className="muted-sub">/{order.quantity}</span>
                </td>
                <td className="cell-ellipsis">
                  {traderName(order.participantId, participantNameById)}
                  <span className="muted-sub"> · {companyNameById.get(order.companyId) ?? `#${order.companyId}`}</span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}

const TYPE_ABBR = { Individual: 'IND', Company: 'CO', AIAgent: 'AI' }

function ParticipantsPanel({ participants }) {
  return (
    <Panel title="Traders" count={`${participants.length}`} className="panel-traders">
      {participants.length === 0 ? (
        <p className="note">No participants yet.</p>
      ) : (
        <div className="tbl-scroll">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Trader</th>
                <th scope="col" className="ta-r">
                  Shares
                </th>
                <th scope="col" className="ta-r">
                  Available
                </th>
              </tr>
            </thead>
            <tbody>
              {participants.map((participant) => (
                <tr key={participant.id}>
                  <th scope="row" className="cell-trader">
                    <span className="cell-ellipsis">{participant.name}</span>
                    <span className="tag">{TYPE_ABBR[participant.type] ?? participant.type}</span>
                  </th>
                  <td className="num ta-r">{formatInt(participant.sharesOwned)}</td>
                  <td className="num ta-r">{formatMoney(participant.availableBalance)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function TradeTapePanel({ transactions, participantNameById, companyNameById }) {
  const rows = transactions.slice(0, 14)
  return (
    <Panel title="Trade tape" count={`${transactions.length} settled`} className="panel-tape">
      {transactions.length === 0 ? (
        <p className="note">No trades yet.</p>
      ) : (
        <div className="tbl-scroll">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Company</th>
                <th scope="col">Flow</th>
                <th scope="col" className="ta-r">
                  Qty
                </th>
                <th scope="col" className="ta-r">
                  Price
                </th>
                <th scope="col" className="ta-r">
                  Total
                </th>
              </tr>
            </thead>
            <tbody>
              {rows.map((transaction) => (
                <tr key={transaction.id}>
                  <th scope="row" className="cell-ellipsis">
                    {companyNameById.get(transaction.companyId) ?? `#${transaction.companyId}`}
                  </th>
                  <td className="cell-flow cell-ellipsis">
                    {traderName(transaction.sellerId, participantNameById)}
                    <span className="flow-arrow" aria-label="to">
                      →
                    </span>
                    {traderName(transaction.buyerId, participantNameById)}
                  </td>
                  <td className="num ta-r">{formatInt(transaction.quantity)}</td>
                  <td className="num ta-r">{formatMoney(transaction.price)}</td>
                  <td className="num ta-r">{formatMoney(transaction.totalCost)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function PlaceOrderPanel({ participants, companies, pending, runAction }) {
  const [participantId, setParticipantId] = useState('')
  const [companyId, setCompanyId] = useState('')
  const [type, setType] = useState('Buy')
  const [quantity, setQuantity] = useState('1')
  const [limitPrice, setLimitPrice] = useState('100')

  const resolvedParticipantId = participantId || participants[0]?.id || ''
  const resolvedCompanyId = companyId || companies[0]?.id || ''
  const reservation = type === 'Buy' ? Number(quantity) * Number(limitPrice) : null

  function handleSubmit(event) {
    event.preventDefault()
    runAction(() =>
      api.placeOrder({
        participantId: Number(resolvedParticipantId),
        companyId: Number(resolvedCompanyId),
        type,
        quantity: Number(quantity),
        limitPrice: Number(limitPrice),
      }),
    )
  }

  return (
    <Panel title="Place order" count="Manual" className="panel-order">
      <form className="order-form" onSubmit={handleSubmit}>
        <div className="side-toggle" role="group" aria-label="Order side">
          <button
            type="button"
            className={`side-btn side-buy ${type === 'Buy' ? 'is-on' : ''}`}
            aria-pressed={type === 'Buy'}
            onClick={() => setType('Buy')}
          >
            Buy
          </button>
          <button
            type="button"
            className={`side-btn side-sell ${type === 'Sell' ? 'is-on' : ''}`}
            aria-pressed={type === 'Sell'}
            onClick={() => setType('Sell')}
          >
            Sell
          </button>
        </div>

        <label className="field">
          <span>Trader</span>
          <select
            className="select"
            value={resolvedParticipantId}
            onChange={(event) => setParticipantId(event.target.value)}
          >
            {participants.map((participant) => (
              <option key={participant.id} value={participant.id}>
                {participant.name}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Company</span>
          <select
            className="select"
            value={resolvedCompanyId}
            onChange={(event) => setCompanyId(event.target.value)}
          >
            {companies.map((company) => (
              <option key={company.id} value={company.id}>
                {company.name}
              </option>
            ))}
          </select>
        </label>
        <div className="field-row">
          <label className="field">
            <span>Quantity</span>
            <input
              className="select num"
              type="number"
              min="1"
              value={quantity}
              onChange={(event) => setQuantity(event.target.value)}
            />
          </label>
          <label className="field">
            <span>Limit price</span>
            <input
              className="select num"
              type="number"
              min="0"
              step="0.01"
              value={limitPrice}
              onChange={(event) => setLimitPrice(event.target.value)}
            />
          </label>
        </div>

        {reservation != null ? (
          <p className="order-hint">
            Reserves <span className="num">{formatMoney(Number.isFinite(reservation) ? reservation : 0)}</span> of cash
          </p>
        ) : null}

        <button className="btn btn-primary btn-block" type="submit" disabled={pending || participants.length === 0}>
          Submit {type.toLowerCase()} order
        </button>
      </form>
    </Panel>
  )
}

export default App
