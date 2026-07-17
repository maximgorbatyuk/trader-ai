import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from './api'
import { formatInt, formatMoney, formatSigned, formatSignedInt, toneOf } from './format'
import { Pager, SortHeader } from './TableControls'
import { useClientTable } from './useClientTable'
import { CASH_LABEL, CASH_TONE } from './cashMovements'
import { Modal } from './Modal'
import { MoneyTransactionModal } from './MoneyTransactionModal'
import { SettlementsTable } from './SettlementsTable'
import { cashSettlement } from './marketAccounting'
import { FavoriteCompaniesTable } from './FavoriteCompaniesTable'
import { favoriteCompanies } from './favoriteCompanies'

const POLL_INTERVAL_MS = 1000
const CASH_MOVEMENT_PAGE_SIZE = 10
const SETTLEMENTS_PAGE_SIZE = 10
const OPEN_STATUSES = new Set(['Open', 'PartiallyFilled'])
const CHANGE_GLYPH = { up: '▲', down: '▼', flat: '◆' }
const MEMBER_TYPE_LABEL = { Individual: 'Individual', Company: 'Company', AIAgent: 'AI agent', CollectiveFund: 'Collective fund', Player: 'Player' }

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
// fund's participant detail and worth history so the Fund tab shows the same balances and performance figures as
// the Player tab without a dedicated endpoint.
function fundSubjectOf(fundDetail, worthHistory) {
  const totalWorth = fundDetail.totalWorth
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
    settledCashBalance: fundDetail.settledCashBalance,
    unsettledCashBalance: fundDetail.unsettledCashBalance,
    reservedBalance: fundDetail.reservedBalance,
    availableBalance: fundDetail.availableBalance,
    loanLiability: fundDetail.loanLiability,
    margin: fundDetail.margin,
    totalWorth,
    overallMoneyChange: fundDetail.currentBalance - fundDetail.initialBalance,
    overallWorthChange: totalWorth - fundDetail.initialBalance,
    lastCycleMoneyChange,
    lastCycleWorthChange,
  }
}

// The player's live control surface. Which actor it shows follows the shared actor selection (`actorKind`)
// owned by the app shell, whose sidebar carries the Player/Managed-fund switch. It polls only the active
// actor's dataset and renders the passed-in market map and order book inside its detail tabs.
export function PlayerPanel({ companies, onSelectCompany, actorKind, orderBook, marketMap }) {
  const [loading, setLoading] = useState(true)
  const [player, setPlayer] = useState(null)
  const [fundDetail, setFundDetail] = useState(null)
  const [orders, setOrders] = useState([])
  const [attention, setAttention] = useState([])
  const [loans, setLoans] = useState([])
  const [loanStatus, setLoanStatus] = useState('active')
  const [worthHistory, setWorthHistory] = useState([])
  const [cashMoves, setCashMoves] = useState([])
  const [settlements, setSettlements] = useState([])
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
        const [orderData, attentionData, loanData, worthData, cashData, settlementData, fundData] = await Promise.all([
          api.getParticipantOrders(activeId, 20),
          api.getCompaniesAttention(activeId),
          api.getParticipantLoans(activeId, { status: loanStatus }),
          api.getParticipantWorthHistory(activeId),
          api.getParticipantMoneyTransactions(activeId, 20),
          api.getParticipantSettlements(activeId),
          actorKind === 'fund' ? api.getParticipant(activeId) : Promise.resolve(null),
        ])
        if (!mountedRef.current) return
        setOrders(orderData)
        setAttention(attentionData)
        setLoans(loanData ?? [])
        setWorthHistory(worthData ?? [])
        setCashMoves(cashData ?? [])
        setSettlements(settlementData?.items ?? [])
        setFundDetail(fundData)
      } else {
        setOrders([])
        setAttention([])
        setLoans([])
        setWorthHistory([])
        setCashMoves([])
        setSettlements([])
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

  const onFund = actorKind === 'fund'
  const hasFund = player?.fundParticipantId != null

  return (
    <div className="player-panel">
      {loading ? (
        <p className="note">Loading the player…</p>
      ) : player === null ? (
        <div className="player-onboarding-map">{marketMap}</div>
      ) : !onFund ? (
        <ActorView
          key="player"
          subject={player}
          participantId={player.id}
          player={player}
          marketMap={marketMap}
          orderBook={orderBook}
          canCancelOrders
          orders={orders}
          attention={attention}
          loans={loans}
          loanStatus={loanStatus}
          onLoanStatusChange={setLoanStatus}
          cashMoves={cashMoves}
          settlements={settlements}
          companies={companies}
          showFavoriteCompanies
          onSelectCompany={onSelectCompany}
          onRefresh={refresh}
        />
      ) : !hasFund ? (
        <OpenFundForm player={player} onRefresh={refresh} />
      ) : fundDetail == null ? (
        <p className="note">Loading the fund…</p>
      ) : (
        <ActorView
          key="fund"
          subject={fundSubjectOf(fundDetail, worthHistory)}
          participantId={player.fundParticipantId}
          player={player}
          marketMap={marketMap}
          orderBook={orderBook}
          isFund
          canCancelOrders
          members={fundDetail.collectiveFundMembers ?? []}
          orders={orders}
          attention={attention}
          loans={loans}
          loanStatus={loanStatus}
          onLoanStatusChange={setLoanStatus}
          cashMoves={cashMoves}
          settlements={settlements}
          companies={companies}
          showFavoriteCompanies
          onSelectCompany={onSelectCompany}
          onRefresh={refresh}
        />
      )}
    </div>
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

// The fund-management dialog opened from the summary column's Fund settings button: move cash between the
// player and the fund, buy an advertisement, or close the fund. A successful close dissolves the fund, which
// unmounts this dialog on the next refresh.
function FundSettingsModal({ player, onRefresh, onClose }) {
  const [amount, setAmount] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState(null)
  const [confirmingClose, setConfirmingClose] = useState(false)
  const [adQuote, setAdQuote] = useState(null)
  const [adBusy, setAdBusy] = useState(false)
  const [adError, setAdError] = useState(null)

  const withdrawable = player.fundWithdrawable ?? 0
  const amountNum = Number(amount)
  const titleId = 'fund-settings-modal-title'

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

  // Fetching the quote is a preview step: the player sees the cost before any cash moves, then confirms to pay.
  async function fetchAdQuote() {
    setAdError(null)
    setAdBusy(true)
    try {
      setAdQuote(await api.getFundAdvertiseQuote(player.fundParticipantId))
    } catch (quoteError) {
      setAdError(quoteError.message)
    } finally {
      setAdBusy(false)
    }
  }

  async function confirmAdvertise() {
    setAdError(null)
    setAdBusy(true)
    try {
      await api.advertiseFund(player.fundParticipantId)
      setAdQuote(null)
      await onRefresh()
    } catch (advertiseError) {
      setAdError(advertiseError.message)
    } finally {
      setAdBusy(false)
    }
  }

  // A successful close removes the fund, so this dialog unmounts on refresh; only reset busy on failure.
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
    <Modal titleId={titleId} onClose={onClose}>
      <header className="modal-head">
        <div className="command-id">
          <span className="command-label">Fund</span>
          <h2 className="command-name" id={titleId}>
            {player.fundName ?? 'Fund settings'}
          </h2>
        </div>
        <button type="button" className="btn select-sm" onClick={onClose}>
          Close
        </button>
      </header>
      <div className="modal-body">
        <div className="modal-section">
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
        </div>
        <div className="modal-section">
          <div className="player-panel-head">
            <span className="map-stat-label">Advertise</span>
            <span className="num" title="How visible the fund is to would-be joiners">
              Popularity {formatInt(player.fundPopularityIndex ?? 0)}
            </span>
          </div>
          <p className="note note-sm">
            A paid advertisement lifts the fund&apos;s popularity, drawing more traders to join. It is paid from the
            fund&apos;s cash and costs less the more the fund has grown.
          </p>
          {adError ? (
            <p className="command-error" role="alert">
              {adError}
            </p>
          ) : null}
          {adQuote ? (
            <>
              <p className="note note-sm">
                Advertising now costs {formatMoney(adQuote.price)} ({(adQuote.fraction * 100).toFixed(2)}% of fund worth),
                set by {adQuote.growthPct.toFixed(1)}% growth over the last 20 cycles.
              </p>
              <div className="order-actions">
                <button type="button" className="btn btn-primary" disabled={adBusy} onClick={confirmAdvertise}>
                  {adBusy ? 'Paying…' : 'Confirm & pay'}
                </button>
                <button type="button" className="btn" disabled={adBusy} onClick={() => setAdQuote(null)}>
                  Cancel
                </button>
              </div>
            </>
          ) : (
            <button type="button" className="btn" disabled={adBusy} onClick={fetchAdQuote}>
              {adBusy ? 'Checking…' : 'Advertise fund'}
            </button>
          )}
        </div>
        <div className="modal-section">
          {confirmingClose ? (
            <>
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
            </>
          ) : (
            <button type="button" className="btn" onClick={() => setConfirmingClose(true)}>
              Close fund
            </button>
          )}
        </div>
      </div>
    </Modal>
  )
}

// A market-tone class ('up'/'down') that stays on for one poll after `value` moves, then clears, so a number
// briefly flashes green up or red down when it changes. Returns null when steady or on the first render.
function useChangeFlash(value) {
  const [tone, setTone] = useState(null)
  const previous = useRef(value)
  const timerRef = useRef(null)

  useEffect(() => {
    if (previous.current != null && typeof value === 'number' && value !== previous.current) {
      setTone(value > previous.current ? 'up' : 'down')
      clearTimeout(timerRef.current)
      timerRef.current = setTimeout(() => setTone(null), POLL_INTERVAL_MS)
    }
    previous.current = value
    return () => clearTimeout(timerRef.current)
  }, [value])

  return tone
}

function GearIcon() {
  return (
    <svg className="icon-gear" viewBox="0 0 24 24" aria-hidden="true">
      <path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.38a2 2 0 0 0-.73-2.73l-.15-.09a2 2 0 0 1-1-1.74v-.51a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2Z" />
      <circle cx="12" cy="12" r="3" />
    </svg>
  )
}

// One actor's summary and detail sub-tabs, split into a compact left column and the wider tab column on the
// right. Reused for the player and the fund; the fund variant (`isFund`) adds the Members sub-tab and a Fund
// settings dialog. The full balance sheet moves behind a click on the total-worth number. Both actors cancel
// their open orders through the player-scoped cancel endpoint, which accepts the player's and its fund's orders.
function ActorView({
  subject,
  participantId,
  player,
  isFund,
  orderBook,
  marketMap,
  canCancelOrders,
  members,
  orders,
  attention,
  loans,
  loanStatus,
  onLoanStatusChange,
  cashMoves,
  settlements,
  companies,
  showFavoriteCompanies,
  onSelectCompany,
  onRefresh,
}) {
  const [balancesOpen, setBalancesOpen] = useState(false)
  const [fundSettingsOpen, setFundSettingsOpen] = useState(false)
  const openOrders = orders.filter((order) => OPEN_STATUSES.has(order.status))
  const cash = cashSettlement(subject.currentBalance, subject.settledCashBalance)
  const worthFlash = useChangeFlash(subject.totalWorth)

  return (
    <>
      <div className="player-header">
        <div className="player-header-main">
          <div className="player-header-id">
            <span className="command-name">{subject.name}</span>
            <button
              type="button"
              className={`quote-last num quote-worth-btn${worthFlash ? ` flash-${worthFlash}` : ''}`}
              onClick={() => setBalancesOpen(true)}
              title="Show full balances"
            >
              {formatMoney(subject.totalWorth)}
            </button>
          </div>
        </div>

        {isFund ? (
          <button type="button" className="btn fund-settings-btn" onClick={() => setFundSettingsOpen(true)}>
            <GearIcon />
            Fund settings
          </button>
        ) : null}
      </div>

      <ActorTabs
        participantId={participantId}
        canCancelOrders={canCancelOrders}
        orderBook={orderBook}
        marketMap={marketMap}
        members={members}
        attention={attention}
        openOrders={openOrders}
        loans={loans}
        loanStatus={loanStatus}
        onLoanStatusChange={onLoanStatusChange}
        cashMoves={cashMoves}
        settlements={settlements}
        companies={companies}
        showFavoriteCompanies={showFavoriteCompanies}
        onSelectCompany={onSelectCompany}
        onRefresh={onRefresh}
      />

      {balancesOpen ? (
        <BalancesModal subject={subject} cash={cash} onClose={() => setBalancesOpen(false)} />
      ) : null}
      {fundSettingsOpen && player ? (
        <FundSettingsModal player={player} onRefresh={onRefresh} onClose={() => setFundSettingsOpen(false)} />
      ) : null}
    </>
  )
}

const PERFORMANCE_STATS = [
  { key: 'cashOverall', label: 'Cash · overall', field: 'overallMoneyChange' },
  { key: 'cashLast', label: 'Cash · last cycle', field: 'lastCycleMoneyChange' },
  { key: 'worthOverall', label: 'Worth · overall', field: 'overallWorthChange' },
  { key: 'worthLast', label: 'Worth · last cycle', field: 'lastCycleWorthChange' },
]

// Cash and worth performance (overall and last cycle) as compact label/value stats rather than a table, shown
// in the balances dialog; the same figures live in the sidebar wallet for the active actor.
function PerformanceStats({ subject }) {
  const lastCycleMissing = subject.lastCycleMoneyChange == null || subject.lastCycleWorthChange == null
  return (
    <div className="modal-section player-section">
      <span className="map-stat-label">Performance</span>
      <dl className="summary-stats">
        {PERFORMANCE_STATS.map((stat) => (
          <div className="summary-stat" key={stat.key}>
            <dt>{stat.label}</dt>
            <dd>
              <ChangeAmount value={subject[stat.field]} />
            </dd>
          </div>
        ))}
      </dl>
      {lastCycleMissing ? (
        <p className="note note-sm">Last-cycle figures appear after one completed cycle.</p>
      ) : null}
    </div>
  )
}

// The full balance sheet plus performance, opened from the total-worth number so the summary column stays compact.
function BalancesModal({ subject, cash, onClose }) {
  const titleId = 'balances-modal-title'

  return (
    <Modal titleId={titleId} onClose={onClose}>
      <header className="modal-head">
        <div className="command-id">
          <span className="command-label">Balances</span>
          <h2 className="command-name" id={titleId}>
            {subject.name}
          </h2>
        </div>
        <button type="button" className="btn select-sm" onClick={onClose}>
          Close
        </button>
      </header>
      <div className="modal-body">
        <div className="modal-section">
          <span className="map-stat-label">Balances</span>
          <dl className="kv">
            <div className="kv-row">
              <dt>Initial balance</dt>
              <dd className="num">{formatMoney(subject.initialBalance)}</dd>
            </div>
            <div className="kv-row">
              <dt>Total cash</dt>
              <dd className="num">{formatMoney(cash.total)}</dd>
            </div>
            <div className="kv-row kv-sub">
              <dt>Settled cash</dt>
              <dd className="num">{formatMoney(cash.settled)}</dd>
            </div>
            <div className="kv-row kv-sub">
              <dt>Pending cash</dt>
              <dd className="num">{formatMoney(cash.pending)}</dd>
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
                <dt>Explicit term-loan debt</dt>
                <dd className="num tone-down">−{formatMoney(subject.loanLiability)}</dd>
              </div>
            ) : null}
            <div className="kv-row kv-sub"><dt>Account equity</dt><dd className="num">{formatMoney(subject.margin?.accountEquity ?? subject.totalWorth)}</dd></div>
            <div className="kv-row kv-sub"><dt>Margin debit</dt><dd className="num">{formatMoney(subject.margin?.debitBalance ?? 0)}</dd></div>
            <div className="kv-row kv-sub"><dt>Margin interest</dt><dd className="num">{formatMoney(subject.margin?.accruedInterest ?? 0)}</dd></div>
            <div className="kv-row kv-sub"><dt>Buying power</dt><dd className="num">{formatMoney(subject.margin?.buyingPower ?? subject.availableBalance)}</dd></div>
            <div className="kv-row kv-total">
              <dt>Total worth</dt>
              <dd className="num">{formatMoney(subject.totalWorth)}</dd>
            </div>
          </dl>
        </div>
        <PerformanceStats subject={subject} />
      </div>
    </Modal>
  )
}

const BASE_TABS = [
  { key: 'map', label: 'Market map', hasCount: false },
  { key: 'orderbook', label: 'Order book', hasCount: false },
  { key: 'attention', label: 'Needs attention', hasCount: true },
  { key: 'orders', label: 'Open orders', hasCount: true },
  { key: 'cash', label: 'Cash movements', hasCount: false },
  { key: 'settlements', label: 'Settlements', hasCount: true },
  { key: 'loans', label: 'Term loans', hasCount: true },
]
const MEMBERS_TAB = { key: 'members', label: 'Members', hasCount: true }
const FAVORITES_TAB = { key: 'favorites', label: 'Favorite companies', hasCount: true }

// The actor's detail views behind one tab strip so the panel stays compact: the roster tabs carry a live count,
// and arrow keys move focus between tabs (roving tabindex) to match the order-book tablist. The fund variant
// appends a Members tab.
function ActorTabs({ participantId, canCancelOrders, orderBook, marketMap, members, attention, openOrders, loans, loanStatus, onLoanStatusChange, cashMoves, settlements, companies, showFavoriteCompanies, onSelectCompany, onRefresh }) {
  const playerTabs = showFavoriteCompanies ? [...BASE_TABS, FAVORITES_TAB] : BASE_TABS
  const tabs = members ? [...playerTabs, MEMBERS_TAB] : playerTabs
  const [activeKey, setActiveKey] = useState('map')
  const tabRefs = useRef({})

  const counts = {
    attention: attention.length,
    orders: openOrders.length,
    loans: loans.length,
    settlements: settlements.length,
    members: members?.length ?? 0,
    favorites: favoriteCompanies(companies).length,
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
        {activeKey === 'map' ? marketMap : null}
        {activeKey === 'orderbook' ? orderBook : null}
        {activeKey === 'attention' ? <AttentionSection attention={attention} onSelectCompany={onSelectCompany} /> : null}
        {activeKey === 'orders' ? (
          <OpenOrdersSection orders={openOrders} companies={companies} canCancel={canCancelOrders} onCancelled={onRefresh} />
        ) : null}
        {activeKey === 'cash' ? <CashMovesTab moves={cashMoves} participantId={participantId} /> : null}
        {activeKey === 'settlements' ? (
          <SettlementsSection settlements={settlements} onSelectCompany={onSelectCompany} />
        ) : null}
        {activeKey === 'loans' ? (
          <LoansSection loans={loans} status={loanStatus} onStatusChange={onLoanStatusChange} onRepaid={onRefresh} />
        ) : null}
        {activeKey === 'favorites' ? (
          <div className="modal-section player-section">
            <FavoriteCompaniesTable companies={companies} onSelectCompany={onSelectCompany} />
          </div>
        ) : null}
        {activeKey === 'members' ? <MembersSection members={members ?? []} /> : null}
      </div>
    </div>
  )
}

// The settlements queue is paginated client-side over the fetched array so the tab never grows past the height
// of one page; the rows already arrive newest-first, so no sort is applied.
function SettlementsSection({ settlements, onSelectCompany }) {
  const { pageRows, page, pageCount, setPage } = useClientTable(settlements, { pageSize: SETTLEMENTS_PAGE_SIZE })

  return (
    <div className="modal-section player-section">
      <SettlementsTable settlements={pageRows} onSelectCompany={onSelectCompany} />
      <Pager page={page} pageCount={pageCount} onPage={setPage} />
    </div>
  )
}

function CashMovesTab({ moves, participantId }) {
  const [selectedMove, setSelectedMove] = useState(null)
  const { pageRows, page, pageCount, setPage } = useClientTable(moves, { pageSize: CASH_MOVEMENT_PAGE_SIZE })

  return (
    <div className="modal-section player-section">
      {moves.length === 0 ? (
        <p className="note note-sm">No cash movements yet.</p>
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
                {pageRows.map((move) => (
                  <tr
                    key={move.id}
                    className="tbl-row-click"
                    role="button"
                    tabIndex={0}
                    aria-label={`Open details for ${CASH_LABEL[move.type] ?? move.type} of ${formatMoney(move.amount)}`}
                    onClick={() => setSelectedMove(move)}
                    onKeyDown={(event) => {
                      if (event.key === 'Enter' || event.key === ' ') {
                        event.preventDefault()
                        setSelectedMove(move)
                      }
                    }}
                  >
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
          <Pager page={page} pageCount={pageCount} onPage={setPage} />
        </>
      )}
      {selectedMove ? (
        <MoneyTransactionModal
          transaction={selectedMove}
          participantId={participantId}
          onClose={() => setSelectedMove(null)}
        />
      ) : null}
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
                  <SortHeader
                    label="Leave in"
                    columnKey="leaveCountdownTradingDays"
                    sortKey={sortKey}
                    sortDir={sortDir}
                    onToggle={toggleSort}
                    title="Trading days until the member becomes eligible to leave (negative), then trading days past that point (positive)."
                  />
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
                    <td className="num ta-r">
                      <MemberLeaveCountdown member={member} />
                    </td>
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

// A member's standing relative to leave eligibility uses trading days so market pauses and intraday cycles do
// not shorten the lock. Founders keep their label because they never switch away.
function MemberLeaveCountdown({ member }) {
  if (member.isFounder) {
    return <span className="muted-sub">Founder</span>
  }
  if (member.leaveCountdownTradingDays >= 0) {
    return <span className="tag tag-flag">{formatSignedInt(member.leaveCountdownTradingDays)}</span>
  }
  return formatSignedInt(member.leaveCountdownTradingDays)
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
        <p className="note note-sm">{status === 'all' ? 'No explicit term loans.' : 'No active explicit term loans.'}</p>
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
                  Principal due
                </th>
                <th scope="col" className="ta-r">
                  Interest due
                </th>
                <th scope="col" className="ta-r">
                  Fees
                </th>
                <th scope="col" className="ta-r">
                  Total
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
                    <td className={`num ta-r${loan.pastDuePrincipal > 0 ? ' tone-attention' : ' muted-sub'}`}>
                      {formatMoney(loan.pastDuePrincipal)}
                    </td>
                    <td className={`num ta-r${loan.pastDueInterest > 0 ? ' tone-attention' : ' muted-sub'}`}>
                      {formatMoney(loan.pastDueInterest)}
                    </td>
                    <td className={`num ta-r${loan.accruedFees > 0 ? ' tone-attention' : ' muted-sub'}`}>
                      {formatMoney(loan.accruedFees)}
                    </td>
                    <td className="num ta-r">{formatMoney(loan.totalLiability)}</td>
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
