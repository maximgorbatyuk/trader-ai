import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from './api'
import { formatCompactMoney, formatInt, formatMoney, formatSigned, toneOf } from './format'
import { Pager, SortHeader } from './TableControls'
import { useClientTable } from './useClientTable'
import { LineChart } from './LineChart'
import { CASH_LABEL, CASH_TONE } from './cashMovements'
import { IndustryHoldingsTable } from './IndustryHoldingsTable'
import { groupHoldingsByIndustry } from './industryHoldings'

const POLL_INTERVAL_MS = 1000
const WORTH_HISTORY_POINTS = 64
const OPEN_STATUSES = new Set(['Open', 'PartiallyFilled'])
const CHANGE_GLYPH = { up: '▲', down: '▼', flat: '◆' }

// A signed money delta rendered with a market tone plus a glyph, so the sign never rides on colour alone;
// a non-numeric value (fewer than two worth snapshots) shows an em dash.
function ChangeAmount({ value }) {
  if (typeof value !== 'number') {
    return <span className="num tone-flat">—</span>
  }
  const tone = toneOf(value)
  return (
    <span className={`num tone-${tone}`}>
      <span aria-hidden="true">{CHANGE_GLYPH[tone]} </span>
      {formatSigned(value)}
    </span>
  )
}

// The player's live control surface: worth headline, balances, performance, active assets, the companies that
// need attention, and open orders. Owns its own polling so it can be dropped straight into the dashboard.
export function PlayerPanel({ companies, onSelectCompany }) {
  const [loading, setLoading] = useState(true)
  const [player, setPlayer] = useState(null)
  const [holdings, setHoldings] = useState([])
  const [orders, setOrders] = useState([])
  const [attention, setAttention] = useState([])
  const [loans, setLoans] = useState([])
  const [loanStatus, setLoanStatus] = useState('active')
  const [worthHistory, setWorthHistory] = useState([])
  const [cashMoves, setCashMoves] = useState([])
  const mountedRef = useRef(true)

  const refresh = useCallback(async () => {
    try {
      const playerData = await api.getPlayer()
      if (!mountedRef.current) return
      if (playerData) {
        const [holdingsData, orderData, attentionData, loanData, worthData, cashData] = await Promise.all([
          api.getHoldings(playerData.id),
          api.getParticipantOrders(playerData.id, 20),
          api.getCompaniesAttention(playerData.id),
          api.getParticipantLoans(playerData.id, { status: loanStatus }),
          api.getParticipantWorthHistory(playerData.id),
          api.getParticipantMoneyTransactions(playerData.id, 20),
        ])
        if (!mountedRef.current) return
        setHoldings(holdingsData)
        setOrders(orderData)
        setAttention(attentionData)
        setLoans(loanData ?? [])
        setWorthHistory(worthData ?? [])
        setCashMoves(cashData ?? [])
      } else {
        setHoldings([])
        setOrders([])
        setAttention([])
        setLoans([])
        setWorthHistory([])
        setCashMoves([])
      }
      setPlayer(playerData)
    } catch {
      // Keep the last known values when a refresh fails.
    } finally {
      if (mountedRef.current) setLoading(false)
    }
  }, [loanStatus])

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
    }
  }, [])

  useEffect(() => {
    async function poll() {
      await refresh()
    }

    poll()
    const intervalId = setInterval(refresh, POLL_INTERVAL_MS)
    return () => clearInterval(intervalId)
  }, [refresh])

  // Headline delta tracks the last completed cycle; it stays hidden until the first cycle produces a figure.
  const lastCycleWorthChange = player?.lastCycleWorthChange
  const worthTone = typeof lastCycleWorthChange === 'number' ? toneOf(lastCycleWorthChange) : null

  return (
    <div className="player-panel">
      <div className="player-panel-head">
        <div className="command-id">
          <span className="command-label">Player</span>
          <span className="command-name">{player ? player.name : 'Play the market'}</span>
        </div>
        {player ? (
          <div className="quote">
            <strong className="quote-last num">{formatMoney(player.totalWorth)}</strong>
            {worthTone ? (
              <span className={`quote-change num tone-${worthTone}`} title="Change over the last completed cycle">
                <span aria-hidden="true">{CHANGE_GLYPH[worthTone]} </span>
                {formatSigned(lastCycleWorthChange)}
              </span>
            ) : null}
          </div>
        ) : null}
      </div>

      {loading ? (
        <p className="note">Loading the player…</p>
      ) : player === null ? (
        <JoinPanel onJoined={refresh} />
      ) : (
        <PlayerStats
          player={player}
          holdings={holdings}
          orders={orders}
          attention={attention}
          loans={loans}
          loanStatus={loanStatus}
          onLoanStatusChange={setLoanStatus}
          worthHistory={worthHistory}
          cashMoves={cashMoves}
          companies={companies}
          onSelectCompany={onSelectCompany}
          onRefresh={refresh}
        />
      )}
    </div>
  )
}

function JoinPanel({ onJoined }) {
  const [name, setName] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState(null)

  async function handleSubmit(event) {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await api.createPlayer({ name: name.trim() || null })
      await onJoined()
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <form className="modal-section player-section" onSubmit={handleSubmit}>
      <p className="note">
        Join the market as a human trader. You start with a random balance and place buy and sell orders through
        the same order book as everyone else. The market never touches your orders, so you cancel them yourself.
      </p>
      <label className="field">
        <span>Name</span>
        <input
          className="select"
          type="text"
          placeholder="Player"
          value={name}
          onChange={(event) => setName(event.target.value)}
        />
      </label>
      {error ? (
        <p className="command-error" role="alert">
          {error}
        </p>
      ) : null}
      <button type="submit" className="btn btn-primary" disabled={submitting}>
        {submitting ? 'Joining…' : 'Join the market'}
      </button>
    </form>
  )
}

function PlayerStats({ player, holdings, orders, attention, loans, loanStatus, onLoanStatusChange, worthHistory, cashMoves, companies, onSelectCompany, onRefresh }) {
  const openOrders = orders.filter((order) => OPEN_STATUSES.has(order.status))
  const lastCycleMissing = player.lastCycleMoneyChange == null || player.lastCycleWorthChange == null

  return (
    <>
      <div className="modal-section player-section">
        <span className="map-stat-label">Balances</span>
        <dl className="kv">
          <div className="kv-row">
            <dt>Initial balance</dt>
            <dd className="num">{formatMoney(player.initialBalance)}</dd>
          </div>
          <div className="kv-row">
            <dt>Current balance</dt>
            <dd className="num">{formatMoney(player.currentBalance)}</dd>
          </div>
          <div className="kv-row kv-sub">
            <dt>Available</dt>
            <dd className="num">{formatMoney(player.availableBalance)}</dd>
          </div>
          <div className="kv-row kv-sub">
            <dt>Reserved</dt>
            <dd className="num">{formatMoney(player.reservedBalance)}</dd>
          </div>
          {player.loanLiability > 0 ? (
            <div className="kv-row kv-sub">
              <dt>Loan debt</dt>
              <dd className="num tone-down">−{formatMoney(player.loanLiability)}</dd>
            </div>
          ) : null}
          <div className="kv-row kv-total">
            <dt>Total worth</dt>
            <dd className="num">{formatMoney(player.totalWorth)}</dd>
          </div>
        </dl>
      </div>

      <div className="modal-section player-section">
        <span className="map-stat-label">Performance</span>
        <table className="tbl">
          <thead>
            <tr>
              <th scope="col">Change</th>
              <th scope="col" className="ta-r">
                Overall
              </th>
              <th scope="col" className="ta-r">
                Last cycle
              </th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <th scope="row">Money</th>
              <td className="ta-r">
                <ChangeAmount value={player.overallMoneyChange} />
              </td>
              <td className="ta-r">
                <ChangeAmount value={player.lastCycleMoneyChange} />
              </td>
            </tr>
            <tr>
              <th scope="row">Worth</th>
              <td className="ta-r">
                <ChangeAmount value={player.overallWorthChange} />
              </td>
              <td className="ta-r">
                <ChangeAmount value={player.lastCycleWorthChange} />
              </td>
            </tr>
          </tbody>
        </table>
        {lastCycleMissing ? (
          <p className="note note-sm">Last-cycle figures appear after one completed cycle.</p>
        ) : null}
      </div>

      <PlayerTabs
        holdings={holdings}
        attention={attention}
        openOrders={openOrders}
        loans={loans}
        loanStatus={loanStatus}
        onLoanStatusChange={onLoanStatusChange}
        worthHistory={worthHistory}
        cashMoves={cashMoves}
        companies={companies}
        onSelectCompany={onSelectCompany}
        onRefresh={onRefresh}
      />
    </>
  )
}

const PLAYER_TABS = [
  { key: 'assets', label: 'Active assets', hasCount: true },
  { key: 'industries', label: 'By industry', hasCount: true },
  { key: 'attention', label: 'Companies needing attention', hasCount: true },
  { key: 'orders', label: 'Open orders', hasCount: true },
  { key: 'worth', label: 'Total worth chart', hasCount: false },
  { key: 'cash', label: 'Cash movements', hasCount: false },
  { key: 'loans', label: 'Loans', hasCount: true },
]

// The player's detail views behind one tab strip so the panel stays compact: the three roster tabs carry a
// live count, and arrow keys move focus between tabs (roving tabindex) to match the order-book tablist.
function PlayerTabs({ holdings, attention, openOrders, loans, loanStatus, onLoanStatusChange, worthHistory, cashMoves, companies, onSelectCompany, onRefresh }) {
  const [activeKey, setActiveKey] = useState('assets')
  const tabRefs = useRef({})

  const industryRows = groupHoldingsByIndustry(holdings, companies)
  const counts = {
    assets: holdings.length,
    industries: industryRows.length,
    attention: attention.length,
    orders: openOrders.length,
    loans: loans.length,
  }

  function focusTab(key) {
    setActiveKey(key)
    tabRefs.current[key]?.focus()
  }

  function onTabKeyDown(event) {
    if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') return
    event.preventDefault()
    const index = PLAYER_TABS.findIndex((tab) => tab.key === activeKey)
    const delta = event.key === 'ArrowRight' ? 1 : -1
    focusTab(PLAYER_TABS[(index + delta + PLAYER_TABS.length) % PLAYER_TABS.length].key)
  }

  return (
    <div className="modal-section player-tabs">
      <div className="tabs tabbar" role="tablist" aria-label="Player details" onKeyDown={onTabKeyDown}>
        {PLAYER_TABS.map((tab) => {
          const selected = tab.key === activeKey
          return (
            <button
              key={tab.key}
              type="button"
              role="tab"
              id={`playertab-${tab.key}`}
              aria-selected={selected}
              aria-controls={`playerpanel-${tab.key}`}
              tabIndex={selected ? 0 : -1}
              ref={(element) => {
                tabRefs.current[tab.key] = element
              }}
              className={`tab${selected ? ' is-active' : ''}`}
              onClick={() => setActiveKey(tab.key)}
            >
              {tab.label}
              {tab.hasCount ? <span className="num book-tab-count">{counts[tab.key]}</span> : null}
            </button>
          )
        })}
      </div>
      <div
        className="tabpanel"
        role="tabpanel"
        id={`playerpanel-${activeKey}`}
        aria-labelledby={`playertab-${activeKey}`}
      >
        {activeKey === 'assets' ? <HoldingsSection holdings={holdings} onSelectCompany={onSelectCompany} /> : null}
        {activeKey === 'industries' ? (
          <div className="modal-section player-section">
            <IndustryHoldingsTable holdings={holdings} companies={companies} />
          </div>
        ) : null}
        {activeKey === 'attention' ? <AttentionSection attention={attention} onSelectCompany={onSelectCompany} /> : null}
        {activeKey === 'orders' ? <OpenOrdersSection orders={openOrders} companies={companies} onCancelled={onRefresh} /> : null}
        {activeKey === 'worth' ? <WorthChartTab worthHistory={worthHistory} /> : null}
        {activeKey === 'cash' ? <CashMovesTab moves={cashMoves} /> : null}
        {activeKey === 'loans' ? (
          <LoansSection loans={loans} status={loanStatus} onStatusChange={onLoanStatusChange} onRepaid={onRefresh} />
        ) : null}
      </div>
    </div>
  )
}

function WorthChartTab({ worthHistory }) {
  const values = worthHistory.map((point) => point.totalWorth)
  const change = values.length >= 2 ? values.at(-1) - values.at(0) : 0

  return (
    <div className="modal-section player-section">
      {values.length < 2 ? (
        <p className="note note-sm">Not enough history yet. Total worth is recorded once per completed cycle.</p>
      ) : (
        <LineChart values={values.slice(-WORTH_HISTORY_POINTS)} tone={toneOf(change)} formatValue={formatCompactMoney} label="Player total worth over time" />
      )}
    </div>
  )
}

function CashMovesTab({ moves }) {
  return (
    <div className="modal-section player-section">
      {moves.length === 0 ? (
        <p className="note note-sm">No cash movements yet.</p>
      ) : (
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
              {moves.map((move) => (
                <tr key={move.id}>
                  <td className={`tone-${CASH_TONE[move.type] ?? 'flat'}`}>{CASH_LABEL[move.type] ?? move.type}</td>
                  <td className="num ta-r">{formatMoney(move.amount)}</td>
                  <td className="num ta-r">#{move.createdInCycleId}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

function HoldingsSection({ holdings, onSelectCompany }) {
  const rows = holdings.map((holding) => ({ ...holding, pnl: holding.marketValue - holding.costBasis }))
  const { pageRows, sortKey, sortDir, toggleSort, page, pageCount, setPage } = useClientTable(rows, {
    initialSortKey: 'marketValue',
    pageSize: 15,
  })

  return (
    <div className="modal-section player-section">
      {rows.length === 0 ? (
        <p className="note note-sm">No shares held yet.</p>
      ) : (
        <>
          <div className="tbl-wrap">
            <table className="tbl">
              <thead>
                <tr>
                  <th scope="col">Company</th>
                  <SortHeader label="Shares" columnKey="shares" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                  <SortHeader label="Cost paid" columnKey="costBasis" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                  <SortHeader label="Value" columnKey="marketValue" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                  <SortHeader label="P/L" columnKey="pnl" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                </tr>
              </thead>
              <tbody>
                {pageRows.map((holding) => (
                  <tr key={holding.companyId}>
                    <th scope="row">
                      {onSelectCompany ? (
                        <button
                          type="button"
                          className="cell-name-btn cell-ellipsis"
                          onClick={() => onSelectCompany(holding.companyId)}
                          title={`Open ${holding.companyName} details`}
                        >
                          {holding.companyName}
                        </button>
                      ) : (
                        <span className="cell-ellipsis">{holding.companyName}</span>
                      )}
                    </th>
                    <td className="num ta-r">{formatInt(holding.shares)}</td>
                    <td className="num ta-r">{formatMoney(holding.costBasis)}</td>
                    <td className="num ta-r">{formatMoney(holding.marketValue)}</td>
                    <td className={`num ta-r tone-${toneOf(holding.pnl)}`}>{formatSigned(holding.pnl)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <Pager page={page} pageCount={pageCount} onPage={setPage} />
        </>
      )}
    </div>
  )
}

const ATTENTION_FLAGS = [
  { key: 'priceDeclining', label: 'Price ↓', title: 'Price fell in at least 3 of the last 10 cycles' },
  { key: 'badNewsImpact', label: 'Bad news', title: 'Hit by a negative news post or a crisis in the last 20 cycles' },
  { key: 'highRisk', label: 'Risk', title: 'Standing High or Extra risk verdict in the last 20 cycles' },
  { key: 'recentMerge', label: 'Merge', title: 'Completed a reverse merge in the last 20 cycles' },
]

function AttentionSection({ attention, onSelectCompany }) {
  const { pageRows, sortKey, sortDir, toggleSort, page, pageCount, setPage } = useClientTable(attention, {
    initialSortKey: 'marketValue',
  })

  return (
    <div className="modal-section player-section">
      {attention.length === 0 ? (
        <p className="note note-sm">None of your holdings are flagged right now.</p>
      ) : (
        <>
          <div className="tbl-wrap">
            <table className="tbl">
              <thead>
                <tr>
                  <th scope="col">Company</th>
                  <SortHeader label="Price" columnKey="currentPrice" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                  <SortHeader label="Change" columnKey="priceChangePct" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                  <SortHeader label="Shares" columnKey="shares" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                  <SortHeader label="Value" columnKey="marketValue" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                  <th scope="col">Signals</th>
                </tr>
              </thead>
              <tbody>
                {pageRows.map((row) => {
                  const changeTone = toneOf(row.priceChangePct)
                  return (
                    <tr key={row.companyId}>
                      <th scope="row">
                        {onSelectCompany ? (
                          <button
                            type="button"
                            className="cell-name-btn cell-ellipsis"
                            onClick={() => onSelectCompany(row.companyId)}
                            title={`Open ${row.name} details`}
                          >
                            {row.name}
                          </button>
                        ) : (
                          <span className="cell-ellipsis">{row.name}</span>
                        )}
                      </th>
                      <td className="num ta-r">{formatMoney(row.currentPrice)}</td>
                      <td className={`num ta-r tone-${changeTone}`}>
                        {row.priceChangePct > 0 ? '+' : row.priceChangePct < 0 ? '−' : ''}
                        {Math.abs(row.priceChangePct * 100).toFixed(1)}%
                      </td>
                      <td className="num ta-r">{formatInt(row.shares)}</td>
                      <td className="num ta-r">{formatMoney(row.marketValue)}</td>
                      <td>
                        <span className="attention-flags">
                          {ATTENTION_FLAGS.filter((flag) => row[flag.key]).map((flag) => (
                            <span key={flag.key} className="tag tag-flag" title={flag.title}>
                              {flag.label}
                            </span>
                          ))}
                        </span>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          <Pager page={page} pageCount={pageCount} onPage={setPage} />
        </>
      )}
    </div>
  )
}

function LoansSection({ loans, status, onStatusChange, onRepaid }) {
  const [amounts, setAmounts] = useState({})
  const [busyId, setBusyId] = useState(null)
  const [error, setError] = useState(null)

  async function repay(loanId, amount) {
    setError(null)
    setBusyId(loanId)
    try {
      await api.repayLoan(loanId, amount)
      setAmounts((current) => ({ ...current, [loanId]: '' }))
      await onRepaid()
    } catch (repayError) {
      setError(repayError.message)
    } finally {
      setBusyId(null)
    }
  }

  return (
    <div className="modal-section player-section">
      <div className="book-filter">
        <select
          className="select select-sm"
          aria-label="Filter loans by status"
          value={status}
          onChange={(event) => onStatusChange(event.target.value)}
        >
          <option value="active">Active</option>
          <option value="all">All</option>
        </select>
      </div>
      {error ? (
        <p className="command-error" role="alert">
          {error}
        </p>
      ) : null}
      {loans.length === 0 ? (
        <p className="note note-sm">{status === 'all' ? 'No loans.' : 'No active loans.'}</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Bank</th>
                <th scope="col" className="ta-r">
                  Taken
                </th>
                <th scope="col" className="ta-r">
                  Interest/cyc
                </th>
                <th scope="col" className="ta-r">
                  Remain
                </th>
                <th scope="col" className="ta-r">
                  Past due
                </th>
                <th scope="col" className="ta-r">
                  Term
                </th>
                <th scope="col" className="ta-r">
                  Repay
                </th>
              </tr>
            </thead>
            <tbody>
              {loans.map((loan) => {
                const amount = amounts[loan.id] ?? ''
                return (
                  <tr key={loan.id}>
                    <th scope="row" className="cell-ellipsis">
                      {loan.bankName}
                    </th>
                    <td className="num ta-r">{formatMoney(loan.principal)}</td>
                    <td className="num ta-r">
                      {formatMoney(loan.interestPerCycleAmount)}
                      <span className="muted-sub"> {(loan.interestRatePerCycle * 100).toFixed(3)}%</span>
                    </td>
                    <td className="num ta-r">{formatMoney(loan.remainingPrincipal)}</td>
                    <td className={`num ta-r${loan.pastDueAmount > 0 ? ' tone-attention' : ''}`}>
                      {formatMoney(loan.pastDueAmount)}
                    </td>
                    <td className="num ta-r">{loan.isClosed ? '—' : `${formatInt(loan.remainingTermCycles)} cyc`}</td>
                    <td className="ta-r">
                      {loan.isClosed ? (
                        <span className="muted-sub">Closed</span>
                      ) : (
                        <span style={{ display: 'inline-flex', gap: '0.25rem', justifyContent: 'flex-end' }}>
                          <input
                            className="select select-sm"
                            style={{ width: '6rem' }}
                            type="number"
                            min="0"
                            step="0.01"
                            placeholder="Amount"
                            value={amount}
                            onChange={(event) => setAmounts((current) => ({ ...current, [loan.id]: event.target.value }))}
                            aria-label={`Partial repayment amount for the ${loan.bankName} loan`}
                          />
                          <button
                            type="button"
                            className="btn select-sm"
                            disabled={busyId === loan.id || !(Number(amount) > 0)}
                            onClick={() => repay(loan.id, Number(amount))}
                          >
                            Pay
                          </button>
                          <button
                            type="button"
                            className="btn select-sm"
                            disabled={busyId === loan.id}
                            onClick={() => repay(loan.id, null)}
                          >
                            {busyId === loan.id ? '…' : 'Pay all'}
                          </button>
                        </span>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

function OpenOrdersSection({ orders, companies, onCancelled }) {
  const [cancelingId, setCancelingId] = useState(null)
  const [error, setError] = useState(null)
  const companyById = new Map((companies ?? []).map((company) => [company.id, company]))

  async function handleCancel(orderId) {
    setError(null)
    setCancelingId(orderId)
    try {
      await api.cancelPlayerOrder(orderId)
      await onCancelled()
    } catch (cancelError) {
      setError(cancelError.message)
    } finally {
      setCancelingId(null)
    }
  }

  return (
    <div className="modal-section player-section">
      {error ? (
        <p className="command-error" role="alert">
          {error}
        </p>
      ) : null}
      {orders.length === 0 ? (
        <p className="note note-sm">No open orders.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Side</th>
                <th scope="col">Company</th>
                <th scope="col" className="ta-r">
                  Filled
                </th>
                <th scope="col" className="ta-r">
                  Limit
                </th>
                <th scope="col" className="ta-r">
                  Market
                </th>
                <th scope="col" className="ta-r">
                  Action
                </th>
              </tr>
            </thead>
            <tbody>
              {orders.map((order) => {
                const company = companyById.get(order.companyId)
                return (
                <tr key={order.id}>
                  <td className={`tone-${order.type === 'Buy' ? 'up' : 'down'}`}>{order.type}</td>
                  <th scope="row" className="cell-ellipsis">
                    {company?.name ?? `#${order.companyId}`}
                  </th>
                  <td className="num ta-r">
                    {order.filledQuantity}
                    <span className="muted-sub">/{order.quantity}</span>
                  </td>
                  <td className="num ta-r">{formatMoney(order.limitPrice)}</td>
                  <td className="num ta-r">
                    {company?.currentPrice != null ? formatMoney(company.currentPrice) : '—'}
                  </td>
                  <td className="ta-r">
                    <button
                      type="button"
                      className="btn select-sm"
                      disabled={cancelingId === order.id}
                      onClick={() => handleCancel(order.id)}
                    >
                      {cancelingId === order.id ? 'Canceling…' : 'Cancel'}
                    </button>
                  </td>
                </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
