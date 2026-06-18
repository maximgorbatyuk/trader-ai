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

function formatMoney(value) {
  return typeof value === 'number' ? moneyFormatter.format(value) : '—'
}

function App() {
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
    <div className="app-shell">
      <TopNav connected={connected} market={market} />
      <main className="dashboard">
        <section className="overview" aria-labelledby="overview-title">
          <div>
            <p className="section-label">Market overview</p>
            <h1 id="overview-title">Trading dashboard</h1>
            <p className="overview-copy">
              Live simulation state from the trader-ai backend. Run cycles manually or let the
              background loop trade on its own.
            </p>
          </div>
          <MetricGrid
            currentCycleNumber={currentCycleNumber}
            openOrders={openOrders.length}
            trades={transactions.length}
          />
        </section>

        <Toolbar market={market} pending={pending} actionError={actionError} runAction={runAction} />

        {market === null ? (
          <section className="dashboard-grid">
            <article className="panel">
              <div className="empty-state">
                <strong>No market yet</strong>
                <p>Seed the demo market to create companies, participants, and the first cycle.</p>
              </div>
            </article>
          </section>
        ) : (
          <>
            <section className="dashboard-grid">
              <PriceChartPanel
                companies={companies}
                selectedCompanyId={selectedCompanyId}
                onSelect={setSelectedCompanyId}
                prices={prices}
              />
              <CompaniesPanel
                companies={companies}
                selectedCompanyId={selectedCompanyId}
                onSelect={setSelectedCompanyId}
              />
            </section>

            <section className="dashboard-grid">
              <OrderBookPanel
                orders={openOrders}
                participantNameById={participantNameById}
                companyNameById={companyNameById}
              />
              <ParticipantsPanel participants={participants} />
            </section>

            <section className="dashboard-grid">
              <PlaceOrderPanel
                participants={participants}
                companies={companies}
                pending={pending}
                runAction={runAction}
              />
              <TransactionsPanel
                transactions={transactions}
                participantNameById={participantNameById}
                companyNameById={companyNameById}
              />
            </section>
          </>
        )}
      </main>
    </div>
  )
}

function TopNav({ connected, market }) {
  const state = connected ? 'available' : 'unavailable'
  const label = connected ? `Backend live · ${market?.status ?? 'No market'}` : 'Backend unavailable'

  return (
    <header className="top-nav">
      <a className="brand" href="/" aria-label="Trader AI dashboard">
        <span className="brand-mark" aria-hidden="true">
          TA
        </span>
        <span>Trader AI</span>
      </a>
      <nav aria-label="Primary navigation">
        <a href="/">Dashboard</a>
        <a href="#companies">Companies</a>
      </nav>
      <div className={`status status-${state}`} role="status">
        <span aria-hidden="true" />
        {label}
      </div>
    </header>
  )
}

function MetricGrid({ currentCycleNumber, openOrders, trades }) {
  const metrics = [
    { label: 'Current cycle', value: currentCycleNumber > 0 ? `#${currentCycleNumber}` : '—' },
    { label: 'Open orders', value: openOrders },
    { label: 'Trades settled', value: trades },
  ]

  return (
    <div className="metric-grid" aria-label="Summary metrics">
      {metrics.map((metric) => (
        <article className="metric" key={metric.label}>
          <span>{metric.label}</span>
          <strong>{metric.value}</strong>
        </article>
      ))}
    </div>
  )
}

function Toolbar({ market, pending, actionError, runAction }) {
  const running = market?.status === 'Running'

  return (
    <section className="toolbar panel" aria-label="Market controls">
      <div className="toolbar-status">
        <span>Controls</span>
        <strong>{market ? market.name : 'No market'}</strong>
      </div>
      <div className="toolbar-actions">
        {market === null ? (
          <button className="btn btn-primary" disabled={pending} onClick={() => runAction(api.seedMarket)}>
            Seed demo market
          </button>
        ) : (
          <>
            <button className="btn" disabled={pending} onClick={() => runAction(api.runDecisions)}>
              Run decisions
            </button>
            <button className="btn btn-primary" disabled={pending} onClick={() => runAction(api.advanceCycle)}>
              Advance cycle
            </button>
            {running ? (
              <button className="btn btn-muted" disabled={pending} onClick={() => runAction(api.pauseMarket)}>
                Pause
              </button>
            ) : (
              <button className="btn btn-muted" disabled={pending} onClick={() => runAction(api.startMarket)}>
                Start
              </button>
            )}
          </>
        )}
      </div>
      {actionError ? <p className="toolbar-error">{actionError}</p> : null}
    </section>
  )
}

function PriceChartPanel({ companies, selectedCompanyId, onSelect, prices }) {
  const values = prices.map((snapshot) => snapshot.price)
  const last = values.at(-1)
  const low = values.length ? Math.min(...values) : undefined
  const high = values.length ? Math.max(...values) : undefined

  return (
    <article className="panel chart-panel">
      <div className="panel-heading">
        <div>
          <h2>Price history</h2>
          <p>{prices.length} snapshots</p>
        </div>
        <select
          className="select"
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
      </div>

      {values.length < 2 ? (
        <p className="panel-note">Not enough price history yet. Advance a cycle to record trades.</p>
      ) : (
        <>
          <LineChart values={values.slice(-24)} />
          <div className="chart-caption">
            <span>Low {formatMoney(low)}</span>
            <span>High {formatMoney(high)}</span>
            <strong>Last {formatMoney(last)}</strong>
          </div>
        </>
      )}
    </article>
  )
}

function LineChart({ values }) {
  const width = 720
  const height = 260
  const padding = 28
  const min = Math.min(...values)
  const max = Math.max(...values)
  const range = max - min || 1

  const points = values.map((value, index) => ({
    x: padding + (index * (width - padding * 2)) / (values.length - 1),
    y: height - padding - ((value - min) / range) * (height - padding * 2),
  }))

  const line = points.map((point) => `${point.x},${point.y}`).join(' ')
  const area = `${padding},${height - padding} ${line} ${width - padding},${height - padding}`

  return (
    <div className="chart-wrap">
      <svg viewBox="0 0 720 260" role="img" aria-label="Company price history line chart">
        <defs>
          <linearGradient id="chart-fill" x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor="#1f8a5b" stopOpacity="0.18" />
            <stop offset="100%" stopColor="#1f8a5b" stopOpacity="0" />
          </linearGradient>
        </defs>
        <g className="chart-grid">
          <line x1="28" x2="692" y1="52" y2="52" />
          <line x1="28" x2="692" y1="112" y2="112" />
          <line x1="28" x2="692" y1="172" y2="172" />
          <line x1="28" x2="692" y1="232" y2="232" />
        </g>
        <polygon points={area} fill="url(#chart-fill)" />
        <polyline className="chart-line" points={line} />
      </svg>
    </div>
  )
}

function CompaniesPanel({ companies, selectedCompanyId, onSelect }) {
  return (
    <article className="panel companies-panel" id="companies">
      <div className="panel-heading">
        <div>
          <h2>Companies</h2>
          <p>{companies.length} tracked</p>
        </div>
      </div>

      {companies.length === 0 ? (
        <p className="panel-note">No companies yet.</p>
      ) : (
        <ul className="company-list">
          {companies.map((company) => (
            <li key={company.id}>
              <button
                type="button"
                className={`company-row ${company.id === selectedCompanyId ? 'company-row-active' : ''}`}
                onClick={() => onSelect(company.id)}
              >
                <span>{company.name}</span>
                <strong>{formatMoney(company.currentPrice)}</strong>
              </button>
            </li>
          ))}
        </ul>
      )}
    </article>
  )
}

function ParticipantsPanel({ participants }) {
  return (
    <article className="panel">
      <div className="panel-heading">
        <div>
          <h2>Participants</h2>
          <p>{participants.length} traders</p>
        </div>
      </div>

      {participants.length === 0 ? (
        <p className="panel-note">No participants yet.</p>
      ) : (
        <ul className="ledger">
          {participants.map((participant) => (
            <li key={participant.id}>
              <div className="ledger-main">
                <span>{participant.name}</span>
                <small>
                  {participant.type} · {participant.sharesOwned} shares
                </small>
              </div>
              <div className="ledger-figure">
                <strong>{formatMoney(participant.availableBalance)}</strong>
                <small>available</small>
              </div>
            </li>
          ))}
        </ul>
      )}
    </article>
  )
}

function OrderBookPanel({ orders, participantNameById, companyNameById }) {
  return (
    <article className="panel">
      <div className="panel-heading">
        <div>
          <h2>Order book</h2>
          <p>{orders.length} open</p>
        </div>
      </div>

      {orders.length === 0 ? (
        <p className="panel-note">No open orders.</p>
      ) : (
        <ul className="ledger">
          {orders.map((order) => (
            <li key={order.id}>
              <div className="ledger-main">
                <span>
                  <span className={`badge badge-${order.type.toLowerCase()}`}>{order.type}</span>
                  {companyNameById.get(order.companyId) ?? `#${order.companyId}`}
                </span>
                <small>
                  {participantNameById.get(order.participantId) ?? `#${order.participantId}`} ·{' '}
                  {order.quantity - order.filledQuantity}/{order.quantity} left
                </small>
              </div>
              <div className="ledger-figure">
                <strong>{formatMoney(order.limitPrice)}</strong>
                <small>{order.status}</small>
              </div>
            </li>
          ))}
        </ul>
      )}
    </article>
  )
}

function TransactionsPanel({ transactions, participantNameById, companyNameById }) {
  return (
    <article className="panel">
      <div className="panel-heading">
        <div>
          <h2>Recent trades</h2>
          <p>{transactions.length} settled</p>
        </div>
      </div>

      {transactions.length === 0 ? (
        <p className="panel-note">No trades yet.</p>
      ) : (
        <ul className="ledger">
          {transactions.slice(0, 12).map((transaction) => (
            <li key={transaction.id}>
              <div className="ledger-main">
                <span>{companyNameById.get(transaction.companyId) ?? `#${transaction.companyId}`}</span>
                <small>
                  {participantNameById.get(transaction.sellerId) ?? `#${transaction.sellerId}`} →{' '}
                  {participantNameById.get(transaction.buyerId) ?? `#${transaction.buyerId}`} ·{' '}
                  {transaction.quantity} shares
                </small>
              </div>
              <div className="ledger-figure">
                <strong>{formatMoney(transaction.price)}</strong>
                <small>{formatMoney(transaction.totalCost)}</small>
              </div>
            </li>
          ))}
        </ul>
      )}
    </article>
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
    <article className="panel">
      <div className="panel-heading">
        <div>
          <h2>Place order</h2>
          <p>Manual entry</p>
        </div>
      </div>

      <form className="order-form" onSubmit={handleSubmit}>
        <label>
          <span>Participant</span>
          <select className="select" value={resolvedParticipantId} onChange={(event) => setParticipantId(event.target.value)}>
            {participants.map((participant) => (
              <option key={participant.id} value={participant.id}>
                {participant.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span>Company</span>
          <select className="select" value={resolvedCompanyId} onChange={(event) => setCompanyId(event.target.value)}>
            {companies.map((company) => (
              <option key={company.id} value={company.id}>
                {company.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span>Side</span>
          <select className="select" value={type} onChange={(event) => setType(event.target.value)}>
            <option value="Buy">Buy</option>
            <option value="Sell">Sell</option>
          </select>
        </label>
        <label>
          <span>Quantity</span>
          <input className="select" type="number" min="1" value={quantity} onChange={(event) => setQuantity(event.target.value)} />
        </label>
        <label>
          <span>Limit price</span>
          <input className="select" type="number" min="0" step="0.01" value={limitPrice} onChange={(event) => setLimitPrice(event.target.value)} />
        </label>
        <button className="btn btn-primary" type="submit" disabled={pending || participants.length === 0}>
          Submit order
        </button>
      </form>
    </article>
  )
}

export default App
