import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
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
const MEMBER_TYPE_LABEL = { Individual: 'Individual', Company: 'Company', AIAgent: 'AI agent', CollectiveFund: 'Collective fund', Player: 'Player' }
const ACTOR_TABS = [
  { key: 'player', label: 'Player' },
  { key: 'fund', label: "Player's Fund" },
]

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

// The player subject reads straight off the player response; the fund subject is derived client-side from the
// fund's participant detail, holdings, and worth history so the Fund tab shows the same balances and performance
// figures as the Player tab without a dedicated endpoint.
function fundSubjectOf(fundDetail, holdings, worthHistory) {
  const holdingsValue = holdings.reduce((sum, holding) => sum + holding.marketValue, 0)
  const totalWorth = fundDetail.currentBalance + holdingsValue - fundDetail.loanLiability
  let lastCycleMoneyChange = null
  let lastCycleWorthChange = null
  if (worthHistory.length >= 2) {
    const latest = worthHistory[worthHistory.length - 1]
    const prior = worthHistory[worthHistory.length - 2]
    lastCycleMoneyChange = latest.balance - prior.balance
    lastCycleWorthChange = latest.totalWorth - prior.totalWorth
  }
  return {
    name: fundDetail.name,
    initialBalance: fundDetail.initialBalance,
    currentBalance: fundDetail.currentBalance,
    reservedBalance: fundDetail.reservedBalance,
    availableBalance: fundDetail.availableBalance,
    loanLiability: fundDetail.loanLiability,
    totalWorth,
    overallMoneyChange: fundDetail.currentBalance - fundDetail.initialBalance,
    overallWorthChange: totalWorth - fundDetail.initialBalance,
    lastCycleMoneyChange,
    lastCycleWorthChange,
  }
}

// The player's live control surface, split into a Player tab and a Player's-Fund tab. The active tab is the
// shared actor selection (`actorKind`) owned by the app shell, so switching here also flips the sidebar switch
// and the order book. The panel polls only the active actor's dataset.
export function PlayerPanel({ companies, onSelectCompany, actorKind, setActorKind }) {
  const [loading, setLoading] = useState(true)
  const [player, setPlayer] = useState(null)
  const [fundDetail, setFundDetail] = useState(null)
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

      const activeId = playerData
        ? actorKind === 'fund'
          ? playerData.fundParticipantId
          : playerData.id
        : null

      if (activeId != null) {
        const [holdingsData, orderData, attentionData, loanData, worthData, cashData, fundData] = await Promise.all([
          api.getHoldings(activeId),
          api.getParticipantOrders(activeId, 20),
          api.getCompaniesAttention(activeId),
          api.getParticipantLoans(activeId, { status: loanStatus }),
          api.getParticipantWorthHistory(activeId),
          api.getParticipantMoneyTransactions(activeId, 20),
          actorKind === 'fund' ? api.getParticipant(activeId) : Promise.resolve(null),
        ])
        if (!mountedRef.current) return
        setHoldings(holdingsData)
        setOrders(orderData)
        setAttention(attentionData)
        setLoans(loanData ?? [])
        setWorthHistory(worthData ?? [])
        setCashMoves(cashData ?? [])
        setFundDetail(fundData)
      } else {
        setHoldings([])
        setOrders([])
        setAttention([])
        setLoans([])
        setWorthHistory([])
        setCashMoves([])
        setFundDetail(null)
      }
      setPlayer(playerData)
    } catch {
      // Keep the last known values when a refresh fails.
    } finally {
      if (mountedRef.current) setLoading(false)
    }
  }, [loanStatus, actorKind])

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

  const showTabs = !loading && player !== null
  const onFund = actorKind === 'fund'
  const hasFund = player?.fundParticipantId != null

  return (
    <div className="player-panel">
      {showTabs ? <ActorTabStrip actorKind={actorKind} onSelect={setActorKind} /> : null}

      {loading ? (
        <p className="note">Loading the player…</p>
      ) : player === null ? (
        <JoinPanel onJoined={refresh} />
      ) : !onFund ? (
        <ActorView
          key="player"
          subject={player}
          canCancelOrders
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
      ) : !hasFund ? (
        <OpenFundForm player={player} onRefresh={refresh} />
      ) : fundDetail == null ? (
        <p className="note">Loading the fund…</p>
      ) : (
        <>
          <ManageFundSection player={player} onRefresh={refresh} />
          <ActorView
            key="fund"
            subject={fundSubjectOf(fundDetail, holdings, worthHistory)}
            canCancelOrders
            members={fundDetail.collectiveFundMembers ?? []}
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
        </>
      )}
    </div>
  )
}

// Player | Player's Fund. Bound to the shared actor selection; arrow keys move focus (roving tabindex) to match
// the order-book and sub-tab tablists.
function ActorTabStrip({ actorKind, onSelect }) {
  const tabRefs = useRef({})

  function focusTab(key) {
    onSelect(key)
    tabRefs.current[key]?.focus()
  }

  function onKeyDown(event) {
    if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') return
    event.preventDefault()
    focusTab(actorKind === 'player' ? 'fund' : 'player')
  }

  return (
    <div className="tabs tabbar" role="tablist" aria-label="Trade as" onKeyDown={onKeyDown}>
      {ACTOR_TABS.map((tab) => {
        const selected = tab.key === actorKind
        return (
          <button
            key={tab.key}
            type="button"
            role="tab"
            aria-selected={selected}
            tabIndex={selected ? 0 : -1}
            ref={(element) => {
              tabRefs.current[tab.key] = element
            }}
            className={`tab${selected ? ' is-active' : ''}`}
            onClick={() => onSelect(tab.key)}
          >
            {tab.label}
          </button>
        )
      })}
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

function OpenFundForm({ player, onRefresh }) {
  const [seed, setSeed] = useState('')
  const [name, setName] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState(null)

  async function handleSubmit(event) {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await api.openPlayerFund({ seedAmount: Number(seed), name: name.trim() || null })
      setSeed('')
      setName('')
      await onRefresh()
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <form className="modal-section player-section" onSubmit={handleSubmit}>
      <span className="map-stat-label">Create a Fund</span>
      <p className="note note-sm">
        Seed a fund from your cash, then trade through it from the order book or any company page. Other traders may
        join it, and you can deposit or withdraw its free cash any time.
      </p>
      <div className="field-pair">
        <label className="field">
          <span>Seed amount (max {formatMoney(player.availableBalance)})</span>
          <input
            className="select num"
            type="number"
            min="0.01"
            step="0.01"
            placeholder="0.00"
            value={seed}
            onChange={(event) => setSeed(event.target.value)}
            aria-label="Fund seed amount"
          />
        </label>
        <label className="field">
          <span>Name (optional)</span>
          <input
            className="select"
            type="text"
            placeholder={`${player.name}'s Fund`}
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </label>
      </div>
      {error ? (
        <p className="command-error" role="alert">
          {error}
        </p>
      ) : null}
      <button type="submit" className="btn btn-primary" disabled={submitting || !(Number(seed) > 0)}>
        {submitting ? 'Creating…' : 'Create fund'}
      </button>
    </form>
  )
}

function ManageFundSection({ player, onRefresh }) {
  const [amount, setAmount] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState(null)
  const [confirmingClose, setConfirmingClose] = useState(false)

  const withdrawable = player.fundWithdrawable ?? 0
  const amountNum = Number(amount)

  async function move(kind) {
    setError(null)
    setBusy(true)
    try {
      const payload = { amount: amountNum }
      if (kind === 'deposit') {
        await api.depositToPlayerFund(payload)
      } else {
        await api.withdrawFromPlayerFund(payload)
      }
      setAmount('')
      await onRefresh()
    } catch (moveError) {
      setError(moveError.message)
    } finally {
      setBusy(false)
    }
  }

  // A successful close removes the fund, so this component unmounts on refresh; only reset busy on failure.
  async function closeFund() {
    setError(null)
    setBusy(true)
    try {
      await api.closePlayerFund()
      await onRefresh()
    } catch (closeError) {
      setError(closeError.message)
      setBusy(false)
    }
  }

  return (
    <div className="modal-section player-section">
      <span className="map-stat-label">Move cash</span>
      <label className="field">
        <span>Amount (withdraw up to {formatMoney(withdrawable)})</span>
        <input
          className="select num"
          type="number"
          min="0.01"
          step="0.01"
          placeholder="0.00"
          value={amount}
          onChange={(event) => setAmount(event.target.value)}
          aria-label="Fund transfer amount"
        />
      </label>
      {error ? (
        <p className="command-error" role="alert">
          {error}
        </p>
      ) : null}
      <div className="order-actions">
        <button
          type="button"
          className="btn btn-primary"
          disabled={busy || !(amountNum > 0) || amountNum > (player.availableBalance ?? 0)}
          onClick={() => move('deposit')}
        >
          {busy ? '…' : 'Deposit'}
        </button>
        <button
          type="button"
          className="btn"
          disabled={busy || !(amountNum > 0) || amountNum > withdrawable}
          onClick={() => move('withdraw')}
        >
          {busy ? '…' : 'Withdraw'}
        </button>
      </div>
      {confirmingClose ? (
        <div className="modal-section player-section">
          <p className="note note-sm">
            Closing returns members&apos; deposits, hands the fund&apos;s shares and remaining cash to you, and dissolves
            the fund.
          </p>
          <div className="order-actions">
            <button type="button" className="btn" disabled={busy} onClick={closeFund}>
              {busy ? 'Closing…' : 'Confirm close'}
            </button>
            <button type="button" className="btn" disabled={busy} onClick={() => setConfirmingClose(false)}>
              Cancel
            </button>
          </div>
        </div>
      ) : (
        <button type="button" className="btn" onClick={() => setConfirmingClose(true)}>
          Close fund
        </button>
      )}
    </div>
  )
}

// One actor's balances, performance, and detail sub-tabs. Reused for the player and the fund; the fund variant
// passes `members` (adding a Members sub-tab). Both can cancel their open orders through the player-scoped
// cancel endpoint, which accepts the player's own orders and its managed fund's.
function ActorView({
  subject,
  canCancelOrders,
  members,
  holdings,
  orders,
  attention,
  loans,
  loanStatus,
  onLoanStatusChange,
  worthHistory,
  cashMoves,
  companies,
  onSelectCompany,
  onRefresh,
}) {
  const openOrders = orders.filter((order) => OPEN_STATUSES.has(order.status))
  const lastCycleMissing = subject.lastCycleMoneyChange == null || subject.lastCycleWorthChange == null
  const worthTone = typeof subject.lastCycleWorthChange === 'number' ? toneOf(subject.lastCycleWorthChange) : null

  return (
    <>
      <div className="player-panel-head">
        <div className="command-id">
          <span className="command-name">{subject.name}</span>
        </div>
        <div className="quote">
          <strong className="quote-last num">{formatMoney(subject.totalWorth)}</strong>
          {worthTone ? (
            <span className={`quote-change num tone-${worthTone}`} title="Change over the last completed cycle">
              <span aria-hidden="true">{CHANGE_GLYPH[worthTone]} </span>
              {formatSigned(subject.lastCycleWorthChange)}
            </span>
          ) : null}
        </div>
      </div>

      <div className="modal-section player-section">
        <span className="map-stat-label">Balances</span>
        <dl className="kv">
          <div className="kv-row">
            <dt>Initial balance</dt>
            <dd className="num">{formatMoney(subject.initialBalance)}</dd>
          </div>
          <div className="kv-row">
            <dt>Current balance</dt>
            <dd className="num">{formatMoney(subject.currentBalance)}</dd>
          </div>
          <div className="kv-row kv-sub">
            <dt>Available</dt>
            <dd className="num">{formatMoney(subject.availableBalance)}</dd>
          </div>
          <div className="kv-row kv-sub">
            <dt>Reserved</dt>
            <dd className="num">{formatMoney(subject.reservedBalance)}</dd>
          </div>
          {subject.loanLiability > 0 ? (
            <div className="kv-row kv-sub">
              <dt>Loan debt</dt>
              <dd className="num tone-down">−{formatMoney(subject.loanLiability)}</dd>
            </div>
          ) : null}
          <div className="kv-row kv-total">
            <dt>Total worth</dt>
            <dd className="num">{formatMoney(subject.totalWorth)}</dd>
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
                <ChangeAmount value={subject.overallMoneyChange} />
              </td>
              <td className="ta-r">
                <ChangeAmount value={subject.lastCycleMoneyChange} />
              </td>
            </tr>
            <tr>
              <th scope="row">Worth</th>
              <td className="ta-r">
                <ChangeAmount value={subject.overallWorthChange} />
              </td>
              <td className="ta-r">
                <ChangeAmount value={subject.lastCycleWorthChange} />
              </td>
            </tr>
          </tbody>
        </table>
        {lastCycleMissing ? (
          <p className="note note-sm">Last-cycle figures appear after one completed cycle.</p>
        ) : null}
      </div>

      <ActorTabs
        canCancelOrders={canCancelOrders}
        members={members}
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

const BASE_TABS = [
  { key: 'assets', label: 'Active assets', hasCount: true },
  { key: 'industries', label: 'By industry', hasCount: true },
  { key: 'attention', label: 'Companies needing attention', hasCount: true },
  { key: 'orders', label: 'Open orders', hasCount: true },
  { key: 'worth', label: 'Total worth chart', hasCount: false },
  { key: 'cash', label: 'Cash movements', hasCount: false },
  { key: 'loans', label: 'Loans', hasCount: true },
]
const MEMBERS_TAB = { key: 'members', label: 'Members', hasCount: true }

// The actor's detail views behind one tab strip so the panel stays compact: the roster tabs carry a live count,
// and arrow keys move focus between tabs (roving tabindex) to match the order-book tablist. The fund variant
// appends a Members tab.
function ActorTabs({ canCancelOrders, members, holdings, attention, openOrders, loans, loanStatus, onLoanStatusChange, worthHistory, cashMoves, companies, onSelectCompany, onRefresh }) {
  const tabs = members ? [...BASE_TABS, MEMBERS_TAB] : BASE_TABS
  const [activeKey, setActiveKey] = useState('assets')
  const tabRefs = useRef({})

  const industryRows = groupHoldingsByIndustry(holdings, companies)
  const counts = {
    assets: holdings.length,
    industries: industryRows.length,
    attention: attention.length,
    orders: openOrders.length,
    loans: loans.length,
    members: members?.length ?? 0,
  }

  function focusTab(key) {
    setActiveKey(key)
    tabRefs.current[key]?.focus()
  }

  function onTabKeyDown(event) {
    if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') return
    event.preventDefault()
    const index = tabs.findIndex((tab) => tab.key === activeKey)
    const delta = event.key === 'ArrowRight' ? 1 : -1
    focusTab(tabs[(index + delta + tabs.length) % tabs.length].key)
  }

  return (
    <div className="modal-section player-tabs">
      <div className="tabs tabbar" role="tablist" aria-label="Actor details" onKeyDown={onTabKeyDown}>
        {tabs.map((tab) => {
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
        {activeKey === 'orders' ? (
          <OpenOrdersSection orders={openOrders} companies={companies} canCancel={canCancelOrders} onCancelled={onRefresh} />
        ) : null}
        {activeKey === 'worth' ? <WorthChartTab worthHistory={worthHistory} /> : null}
        {activeKey === 'cash' ? <CashMovesTab moves={cashMoves} /> : null}
        {activeKey === 'loans' ? (
          <LoansSection loans={loans} status={loanStatus} onStatusChange={onLoanStatusChange} onRepaid={onRefresh} />
        ) : null}
        {activeKey === 'members' ? <MembersSection members={members ?? []} /> : null}
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
        <LineChart values={values.slice(-WORTH_HISTORY_POINTS)} tone={toneOf(change)} formatValue={formatCompactMoney} label="Total worth over time" />
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
                  <td>
                    <span className={`tone-${CASH_TONE[move.type] ?? 'flat'}`}>{CASH_LABEL[move.type] ?? move.type}</span>
                  </td>
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

// The fund's joiners, paged at 10. Deposits and payouts are sortable; the member name links to its trader page.
function MembersSection({ members }) {
  const { pageRows, sortKey, sortDir, toggleSort, page, pageCount, setPage } = useClientTable(members, {
    initialSortKey: 'deposit',
    pageSize: 10,
  })

  return (
    <div className="modal-section player-section">
      {members.length === 0 ? (
        <p className="note note-sm">No members have joined yet.</p>
      ) : (
        <>
          <div className="tbl-wrap">
            <table className="tbl">
              <thead>
                <tr>
                  <th scope="col">Member</th>
                  <th scope="col">Type</th>
                  <SortHeader label="Joined" columnKey="joinedInCycleNumber" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                  <SortHeader label="Deposit" columnKey="deposit" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                  <SortHeader label="Payouts" columnKey="payouts" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                </tr>
              </thead>
              <tbody>
                {pageRows.map((member) => (
                  <tr key={member.participantId}>
                    <th scope="row" className="cell-ellipsis">
                      <Link className="cell-link" to={`/traders/${member.participantId}`}>
                        {member.name}
                      </Link>
                    </th>
                    <td>
                      <span className="cell-trader">
                        <span className="tag">{MEMBER_TYPE_LABEL[member.type] ?? member.type}</span>
                        {member.isLeaving ? <span className="tag tag-bankrupt">Leaving</span> : null}
                      </span>
                    </td>
                    <td className="num ta-r">cycle {formatInt(member.joinedInCycleNumber)}</td>
                    <td className="num ta-r">{formatMoney(member.deposit)}</td>
                    <td className="num ta-r">{formatMoney(member.payouts)}</td>
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

function OpenOrdersSection({ orders, companies, canCancel, onCancelled }) {
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
                {canCancel ? (
                  <th scope="col" className="ta-r">
                    Action
                  </th>
                ) : null}
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
                  {canCancel ? (
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
                  ) : null}
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
