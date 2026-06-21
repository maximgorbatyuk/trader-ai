import { useCallback, useEffect, useRef, useState } from 'react'
import './App.css'
import { api } from './api'
import { formatCompactMoney, formatInt, formatMoney, formatSigned, toneOf } from './format'
import { Panel } from './Panel'
import { LineChart } from './LineChart'
import { CompanyModal } from './CompanyModal'

const POLL_INTERVAL_MS = 1000
const OPEN_STATUSES = new Set(['Open', 'PartiallyFilled'])
const REPOSITORY_URL = 'https://github.com/maximgorbatyuk/trader-ai'
const FOOTER_LINK_GROUPS = [
  [
    { label: 'Concept', href: `${REPOSITORY_URL}/blob/main/docs/domain.md` },
    { label: 'About', href: `${REPOSITORY_URL}#trader-ai` },
  ],
  [
    { label: 'Github', href: REPOSITORY_URL },
    { label: 'Issues', href: `${REPOSITORY_URL}/issues` },
  ],
]

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
  const [cycleActivity, setCycleActivity] = useState([])
  const [selectedCompanyId, setSelectedCompanyId] = useState(null)
  const [mapModalCompanyId, setMapModalCompanyId] = useState(null)
  const [pending, setPending] = useState(false)
  const [actionError, setActionError] = useState(null)

  const selectedRef = useRef(null)
  useEffect(() => {
    selectedRef.current = selectedCompanyId
  }, [selectedCompanyId])

  const loadAll = useCallback(async () => {
    try {
      const [marketData, companyData, participantData, orderData, cycleData, activityData, transactionData] =
        await Promise.all([
          api.getMarket(),
          api.getCompanies(),
          api.getParticipants(),
          api.getOrders('open'),
          api.getCycles(),
          api.getCycleActivity(),
          api.getShareTransactions(50),
        ])

      setMarket(marketData)
      setCompanies(companyData)
      setParticipants(participantData)
      setOrders(orderData)
      setCycles(cycleData)
      setCycleActivity(activityData)
      setTransactions(transactionData)
      setConnected(true)

      const companyId = selectedRef.current
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

  async function resetMarket() {
    await api.resetMarket()

    selectedRef.current = null
    setSelectedCompanyId(null)
    setMapModalCompanyId(null)
    setPrices([])
  }

  const participantNameById = new Map(participants.map((participant) => [participant.id, participant.name]))
  const companyNameById = new Map(companies.map((company) => [company.id, company.name]))
  const openOrders = orders.filter((order) => OPEN_STATUSES.has(order.status))
  const selectedCompany = companies.find((company) => company.id === selectedCompanyId) ?? null
  const mapModalCompany = companies.find((company) => company.id === mapModalCompanyId) ?? null
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
                  resetMarket={resetMarket}
                />

                <MarketMapPanel
                  companies={companies}
                  participants={participants}
                  lastDividendTotal={market.lastDividendTotal}
                  onSelectCompany={setMapModalCompanyId}
                />

                <ActivityPanel activity={cycleActivity} />

                <div className="grid-orders">
                  <OrderBookPanel
                    orders={openOrders}
                    participantNameById={participantNameById}
                    companyNameById={companyNameById}
                  />
                  <TradeTapePanel
                    transactions={transactions}
                    participantNameById={participantNameById}
                    companyNameById={companyNameById}
                  />
                  <PlaceOrderPanel
                    participants={participants}
                    companies={companies}
                    pending={pending}
                    runAction={runAction}
                  />
                </div>

                <ParticipantsPanel participants={participants} />

                <div className="grid-detail">
                  <WatchlistPanel
                    companies={companies}
                    selectedCompanyId={selectedCompanyId}
                    onSelect={setSelectedCompanyId}
                  />
                  <PriceChartPanel company={selectedCompany} prices={prices} />
                </div>
              </>
            ) : null}
          </>
        )}
      </main>

      <Footer />

      {mapModalCompany ? (
        <CompanyModal
          company={mapModalCompany}
          participantNameById={participantNameById}
          onClose={() => setMapModalCompanyId(null)}
        />
      ) : null}
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

function Footer() {
  return (
    <footer className="footer">
      <div className="footer-brand">
        <p className="footer-title">Trader AI</p>
        <p className="footer-subtitle">
          Made with ❤️, coffee and claude by (c){' '}
          <a href="https://github.com/maximgorbatyuk" target="_blank" rel="noreferrer">
            maximgorbatyuk
          </a>
        </p>
      </div>

      {FOOTER_LINK_GROUPS.map((links, index) => (
        <nav
          className="footer-links"
          aria-label={index === 0 ? 'Project links' : 'Repository links'}
          key={index === 0 ? 'project' : 'repository'}
        >
          <ul>
            {links.map((link) => (
              <li key={link.label}>
                <a href={link.href} target="_blank" rel="noreferrer">
                  {link.label}
                </a>
              </li>
            ))}
          </ul>
        </nav>
      ))}
    </footer>
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
  resetMarket,
}) {
  const running = market.status === 'Running'
  const [confirmingReset, setConfirmingReset] = useState(false)

  useEffect(() => {
    if (!confirmingReset) return undefined

    const timer = setTimeout(() => setConfirmingReset(false), 5000)
    return () => clearTimeout(timer)
  }, [confirmingReset])

  function handleResetMarket() {
    if (!confirmingReset) {
      setConfirmingReset(true)
      return
    }

    setConfirmingReset(false)
    runAction(resetMarket)
  }

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
        <button
          className="btn"
          disabled={pending || running}
          title={running ? 'Stop the loop to step a cycle by hand' : 'Run one decision-and-match cycle'}
          onClick={() => runAction(api.stepCycle)}
        >
          Step once
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
        <button
          className={`btn btn-reset${confirmingReset ? ' btn-reset-armed' : ''}`}
          disabled={pending}
          title={confirmingReset ? 'Click again to erase and reseed the demo database' : 'Erase and reseed the demo database'}
          onClick={handleResetMarket}
        >
          {confirmingReset ? 'Confirm reset' : 'Reset DB'}
        </button>
      </div>

      {actionError ? (
        <p className="command-error" role="alert">
          {actionError}
        </p>
      ) : null}
    </section>
  )
}

function PriceChartPanel({ company, prices }) {
  const values = prices.map((snapshot) => snapshot.price)
  const last = values.at(-1)
  const first = values.at(0)
  const low = values.length ? Math.min(...values) : undefined
  const high = values.length ? Math.max(...values) : undefined
  const change = values.length >= 2 ? last - first : 0
  const changePct = first ? (change / first) * 100 : 0
  const tone = toneOf(change)
  const flash = useChangeFlash(last)

  return (
    <Panel
      title={company ? `Price · ${company.name}` : 'Price'}
      count={company ? `${prices.length} snapshot${prices.length === 1 ? '' : 's'}` : undefined}
      className="panel-chart"
    >
      {!company ? (
        <p className="note">Select a company to see its price history.</p>
      ) : values.length < 2 ? (
        <p className="note">Not enough price history yet. Start the loop or step a cycle to record trades.</p>
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
              <li key={company.id} className="watch-item">
                <button
                  type="button"
                  className={`watch-row ${active ? 'is-active' : ''}`}
                  aria-pressed={active}
                  onClick={() => onSelect(company.id)}
                >
                  <span className="watch-name">{company.name}</span>
                  <span className="watch-price num">{formatMoney(company.currentPrice)}</span>
                </button>
                <a
                  className="open-page open-page-watch"
                  href={`/companies/${company.id}`}
                  target="_blank"
                  rel="noopener"
                  title="Open detail page in a new tab"
                  aria-label={`Open ${company.name} detail page in a new tab`}
                >
                  <span aria-hidden="true">↗</span>
                </a>
              </li>
            )
          })}
        </ul>
      )}
    </Panel>
  )
}

function OrderBookPanel({ orders, participantNameById, companyNameById }) {
  const buys = orders
    .filter((order) => order.type === 'Buy')
    .sort((a, b) => b.limitPrice - a.limitPrice)
  const sells = orders
    .filter((order) => order.type === 'Sell')
    .sort((a, b) => a.limitPrice - b.limitPrice)

  return (
    <Panel title="Order book" count={`${orders.length} open`} className="panel-book">
      <div className="book">
        <OrderSide
          side="Buy"
          tone="up"
          orders={buys}
          participantNameById={participantNameById}
          companyNameById={companyNameById}
        />
        <OrderSide
          side="Sell"
          tone="down"
          orders={sells}
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
        <p className="note note-sm">No {side.toLowerCase()} orders.</p>
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

const TYPE_LABEL = { Individual: 'Individual', Company: 'Company', AIAgent: 'AI' }

// Net worth proxy used for the Total column and its default sort: cash on hand plus the estimated
// market value of shares held.
const TRADER_SORTS = {
  balance: (participant) => participant.currentBalance ?? 0,
  estimation: (participant) => participant.holdingsValue ?? 0,
  total: (participant) => (participant.currentBalance ?? 0) + (participant.holdingsValue ?? 0),
}

function ParticipantsPanel({ participants }) {
  const [sortKey, setSortKey] = useState('total')
  const [sortDir, setSortDir] = useState('desc')

  function toggleSort(key) {
    if (key === sortKey) {
      setSortDir((dir) => (dir === 'desc' ? 'asc' : 'desc'))
    } else {
      setSortKey(key)
      setSortDir('desc')
    }
  }

  const selector = TRADER_SORTS[sortKey]
  const sorted = [...participants].sort((a, b) => {
    const diff = selector(a) - selector(b)
    return sortDir === 'desc' ? -diff : diff
  })

  function sortableHeader(key, label, title) {
    const active = sortKey === key
    return (
      <th scope="col" className="ta-r" aria-sort={active ? (sortDir === 'desc' ? 'descending' : 'ascending') : 'none'}>
        <button
          type="button"
          className={`th-sort ${active ? 'is-active' : ''}`}
          onClick={() => toggleSort(key)}
          title={title}
        >
          {label}
          <span className="th-sort-glyph" aria-hidden="true">
            {active ? (sortDir === 'desc' ? '▼' : '▲') : '↕'}
          </span>
        </button>
      </th>
    )
  }

  return (
    <Panel title="Traders" count={`${participants.length}`} className="panel-traders">
      {participants.length === 0 ? (
        <p className="note">No participants yet.</p>
      ) : (
        <div className="tbl-scroll">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Type</th>
                <th scope="col" className="ta-r">
                  Shares
                </th>
                {sortableHeader('balance', 'Current balance')}
                {sortableHeader('estimation', 'Holdings (est.)', 'Estimated market value of shares held')}
                {sortableHeader('total', 'Total', 'Current balance plus holdings estimation')}
                <th scope="col" className="ta-r">
                  Actions
                </th>
              </tr>
            </thead>
            <tbody>
              {sorted.map((participant) => {
                const estimation = participant.holdingsValue ?? 0
                const total = (participant.currentBalance ?? 0) + estimation
                return (
                  <tr key={participant.id}>
                    <th scope="row" className="cell-ellipsis">
                      {participant.name}
                    </th>
                    <td>
                      <span className="tag">{TYPE_LABEL[participant.type] ?? participant.type}</span>
                    </td>
                    <td className="num ta-r">{formatInt(participant.sharesOwned)}</td>
                    <td className="num ta-r">{formatMoney(participant.currentBalance)}</td>
                    <td className="num ta-r">{formatMoney(estimation)}</td>
                    <td className="num ta-r">{formatMoney(total)}</td>
                    <td className="ta-r">
                      <a
                        className="cell-link"
                        href={`/participants/${participant.id}`}
                        target="_blank"
                        rel="noopener"
                        aria-label={`Open ${participant.name} page in a new tab`}
                      >
                        Open page<span aria-hidden="true"> ↗</span>
                      </a>
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

// Layout box for the treemap; tile positions are emitted as percentages of it, and the panel keeps this
// aspect ratio so the proportions hold at any width.
const MAP_BOX_W = 100
const MAP_BOX_H = 42
const TONE_GLYPH = { up: '▲', down: '▼', flat: '–' }
const TONE_WORD = { up: 'up', down: 'down', flat: 'unchanged' }

function formatPct(value) {
  if (typeof value !== 'number') return '—'
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  return `${sign}${(Math.abs(value) * 100).toFixed(1)}%`
}

function heatMix(value) {
  if (typeof value !== 'number' || value === 0) return '0%'
  return `${Math.min(88, 44 + Math.abs(value) * 900).toFixed(0)}%`
}

function mapTileSize(areaPct, widthPct, heightPct) {
  const shortestSide = Math.min(widthPct, heightPct)
  if (areaPct < 1.1 || shortestSide < 8) return 'is-tiny'
  if (areaPct < 2.2 || shortestSide < 13) return 'is-small'
  return ''
}

// Worst (largest) aspect ratio a row of tile areas would reach if laid along a side of the given length.
function worstRatio(areas, side, sum) {
  if (areas.length === 0 || sum <= 0) return Infinity
  const max = Math.max(...areas)
  const min = Math.min(...areas)
  const side2 = side * side
  const sum2 = sum * sum
  return Math.max((side2 * max) / sum2, sum2 / (side2 * min))
}

// Squarified treemap (Bruls, Huizing, van Wijk): packs items into the box with area proportional to
// value, growing each row only while it keeps tiles close to square. Items must be sorted largest first.
function squarify(items, width, height) {
  const total = items.reduce((sum, item) => sum + item.value, 0)
  if (total <= 0) return []

  const scale = (width * height) / total
  const nodes = items.map((item) => ({ item, area: item.value * scale }))

  const placed = []
  let free = { x: 0, y: 0, w: width, h: height }
  let index = 0

  while (index < nodes.length) {
    const side = Math.min(free.w, free.h)
    const row = []
    let rowSum = 0

    while (index + row.length < nodes.length) {
      const next = nodes[index + row.length]
      const current = row.map((node) => node.area)
      const widened = [...current, next.area]
      if (row.length === 0 || worstRatio(widened, side, rowSum + next.area) <= worstRatio(current, side, rowSum)) {
        row.push(next)
        rowSum += next.area
      } else {
        break
      }
    }

    const thickness = rowSum / side
    if (free.w >= free.h) {
      let y = free.y
      for (const node of row) {
        const cellHeight = node.area / thickness
        placed.push({ ...node.item, x: free.x, y, w: thickness, h: cellHeight })
        y += cellHeight
      }
      free = { x: free.x + thickness, y: free.y, w: free.w - thickness, h: free.h }
    } else {
      let x = free.x
      for (const node of row) {
        const cellWidth = node.area / thickness
        placed.push({ ...node.item, x, y: free.y, w: cellWidth, h: thickness })
        x += cellWidth
      }
      free = { x: free.x, y: free.y + thickness, w: free.w, h: free.h - thickness }
    }

    index += row.length
  }

  return placed
}

// Treemap of the largest companies by capitalisation: tile area tracks market cap, colour tracks the last
// price move (green up, red down, grey flat) with a glyph and signed percent so it is never colour-only.
function MarketMapPanel({ companies, participants, lastDividendTotal, onSelectCompany }) {
  const mappedCompanies = companies
    .map((company) => ({
      ...company,
      capitalization: company.issuedSharesCount * (company.currentPrice ?? 0),
    }))
    .filter((company) => company.capitalization > 0)
    .sort((a, b) => b.capitalization - a.capitalization)
  const totalShares = mappedCompanies.reduce((sum, company) => sum + company.issuedSharesCount, 0)

  // Capitalisation values every issued share at its company's current price, matching the tile areas;
  // participant cash is the cash side of the same market.
  const totalCapitalization = mappedCompanies.reduce((sum, company) => sum + company.capitalization, 0)
  const totalParticipantMoney = participants.reduce(
    (sum, participant) => sum + (participant.currentBalance ?? 0),
    0,
  )

  const tiles = squarify(
    mappedCompanies.map((company) => ({ company, value: company.capitalization })),
    MAP_BOX_W,
    MAP_BOX_H,
  )

  return (
    <Panel
      title="Market map"
      count={mappedCompanies.length ? `${mappedCompanies.length} companies · ${formatInt(totalShares)} shares` : undefined}
      className="panel-map"
    >
      {mappedCompanies.length === 0 ? (
        <p className="note">Seed the market to see company prices.</p>
      ) : (
        <div className="map-layout">
        <div className="market-map" style={{ aspectRatio: `${MAP_BOX_W} / ${MAP_BOX_H}` }}>
          {tiles.map(({ company, x, y, w, h }) => {
            const tone = toneOf(company.priceChangePct)
            const widthPct = (w / MAP_BOX_W) * 100
            const heightPct = (h / MAP_BOX_H) * 100
            const areaPct = (company.capitalization / totalCapitalization) * 100
            const sizeClass = mapTileSize(areaPct, widthPct, heightPct)
            return (
              <div
                key={company.id}
                className={`map-tile tone-bg-${tone} ${sizeClass}`}
                role="button"
                tabIndex={0}
                onClick={() => onSelectCompany(company.id)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault()
                    onSelectCompany(company.id)
                  }
                }}
                style={{
                  left: `${(x / MAP_BOX_W) * 100}%`,
                  top: `${(y / MAP_BOX_H) * 100}%`,
                  width: `${widthPct}%`,
                  height: `${heightPct}%`,
                  '--map-area': areaPct.toFixed(2),
                  '--map-heat': heatMix(company.priceChangePct),
                }}
                title={`${company.name} · ${formatCompactMoney(company.capitalization)} cap · ${formatInt(company.issuedSharesCount)} shares · ${formatMoney(company.currentPrice)} · ${formatPct(company.priceChangePct)}`}
                aria-label={`${company.name}, ${formatCompactMoney(company.capitalization)} capitalisation, ${formatInt(company.issuedSharesCount)} issued shares, ${formatMoney(company.currentPrice)}, ${TONE_WORD[tone]} ${formatPct(company.priceChangePct)}. Open details.`}
              >
                <span className="map-name">{company.name}</span>
                <span className="map-cap num">{formatCompactMoney(company.capitalization)}</span>
                <span className="map-change num">
                  <span aria-hidden="true">{TONE_GLYPH[tone]}</span> {formatPct(company.priceChangePct)}
                </span>
              </div>
            )
          })}
        </div>
        <aside className="map-stats">
          <div className="map-stat">
            <span className="map-stat-label">Total cap</span>
            <span className="map-stat-value num" title={formatMoney(totalCapitalization)}>
              {formatCompactMoney(totalCapitalization)}
            </span>
          </div>
          <div className="map-stat">
            <span className="map-stat-label">Trader cash</span>
            <span className="map-stat-value num" title={formatMoney(totalParticipantMoney)}>
              {formatCompactMoney(totalParticipantMoney)}
            </span>
          </div>
          <div className="map-stat">
            <span className="map-stat-label">Last dividends</span>
            <span className="map-stat-value num" title={formatMoney(lastDividendTotal)}>
              {formatCompactMoney(lastDividendTotal)}
            </span>
          </div>
        </aside>
        </div>
      )}
    </Panel>
  )
}

const ACTIVITY_WINDOW = 48

function ActivityPanel({ activity }) {
  // The first cycle can hold a large backlog of orders, so the chart shows a recent window to keep
  // the scale readable; the total stays all-time.
  const points = activity.slice(-ACTIVITY_WINDOW)
  const total = activity.reduce((sum, point) => sum + point.ordersPlaced, 0)
  const windowCounts = points.map((point) => point.ordersPlaced)
  const latest = windowCounts.at(-1) ?? 0
  const peak = windowCounts.length ? Math.max(...windowCounts) : 0
  const hasDividend = points.some((point) => point.paidDividend)

  return (
    <Panel
      title="Market activity"
      count={`${formatInt(total)} orders`}
      className="panel-activity"
    >
      {points.length < 2 ? (
        <p className="note">Start the loop or step a cycle to see orders placed per loop.</p>
      ) : (
        <>
          <div className="quote">
            <strong className="quote-last num">{formatInt(latest)}</strong>
            <span className="muted-sub">orders last loop · peak {formatInt(peak)} in last {points.length}</span>
            {hasDividend ? <span className="activity-legend">dividend cycle</span> : null}
          </div>
          <ActivityChart points={points} />
        </>
      )}
    </Panel>
  )
}

// Line-and-area chart of orders placed per loop, with a labelled count axis (Y) and cycle axis (X).
function ActivityChart({ points }) {
  const width = 720
  const height = 96
  const margin = { top: 10, right: 12, bottom: 24, left: 44 }
  const plotWidth = width - margin.left - margin.right
  const plotHeight = height - margin.top - margin.bottom

  const counts = points.map((point) => point.ordersPlaced)
  const tickCount = 3
  const step = Math.max(1, Math.ceil(Math.max(...counts) / tickCount))
  const yMax = step * tickCount
  const yTicks = Array.from({ length: tickCount + 1 }, (_, index) => index * step)

  const count = points.length
  const x = (index) => margin.left + (count === 1 ? plotWidth / 2 : (index * plotWidth) / (count - 1))
  const y = (value) => margin.top + plotHeight - (value / yMax) * plotHeight
  const baseline = margin.top + plotHeight

  const line = points.map((point, index) => `${x(index)},${y(point.ordersPlaced)}`).join(' ')
  const area = `${x(0)},${baseline} ${line} ${x(count - 1)},${baseline}`
  const last = points.at(-1)

  const indexed = points.map((point, index) => ({ point, index }))
  // Label even cycle numbers only so they never overlap, always keeping the most recent one.
  const xLabels = indexed.filter(({ point, index }) => point.cycleNumber % 2 === 0 || index === count - 1)
  const dividendLines = indexed.filter(({ point }) => point.paidDividend)

  return (
    <div className="activity-chart">
      <svg
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label={
          `Orders placed per loop across the last ${count} cycles, peaking at ${Math.max(...counts)}.` +
          (dividendLines.length
            ? ` Dividends were paid in cycle${dividendLines.length > 1 ? 's' : ''} ${dividendLines.map(({ point }) => point.cycleNumber).join(', ')}.`
            : '')
        }
      >
        <defs>
          <linearGradient id="activity-fill" x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor="currentColor" stopOpacity="0.18" />
            <stop offset="100%" stopColor="currentColor" stopOpacity="0" />
          </linearGradient>
        </defs>
        <g className="chart-axis" aria-hidden="true">
          {yTicks.map((tick) => (
            <g key={tick}>
              <line className="chart-gridline" x1={margin.left} x2={width - margin.right} y1={y(tick)} y2={y(tick)} />
              <text className="chart-tick chart-tick-y" x={margin.left - 8} y={y(tick)}>
                {tick}
              </text>
            </g>
          ))}
          {indexed.map(({ index }) => (
            <line
              key={`v-${index}`}
              className="chart-gridline"
              x1={x(index)}
              x2={x(index)}
              y1={margin.top}
              y2={baseline}
            />
          ))}
          {xLabels.map(({ point, index }) => (
            <text key={point.cycleNumber} className="chart-tick chart-tick-x" x={x(index)} y={height - 8}>
              {point.cycleNumber}
            </text>
          ))}
        </g>
        <polygon className="activity-area" points={area} fill="url(#activity-fill)" />
        <polyline className="activity-line" points={line} />
        {/* Dashed so the dividend marker reads without relying on colour alone. */}
        {dividendLines.map(({ index }) => (
          <line
            key={`div-${index}`}
            className="chart-dividend-line"
            x1={x(index)}
            x2={x(index)}
            y1={margin.top}
            y2={baseline}
          />
        ))}
        {last ? <circle className="activity-dot" cx={x(count - 1)} cy={y(last.ordersPlaced)} r="3.5" /> : null}
      </svg>
    </div>
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
