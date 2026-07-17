import { useCallback, useEffect, useState } from 'react'
import { Navigate, NavLink, Outlet, useLocation } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt, formatMoney, formatSigned, toneOf } from './format'
import { TopBar } from './Chrome'
import { PlayerOnboarding } from './PlayerOnboarding'
import { loadShellSnapshot } from './appShellModel'
import { createTradingClock, formatTradingClock, interpolateTradingClock, shouldKeepTradingClock } from './tradingClock'

const SHELL_POLL_INTERVAL_MS = 1500
const WORTH_GLYPH = { up: '▲', down: '▼' }
const CHANGE_GLYPH = { up: '▲', down: '▼', flat: '◆' }
const ACTOR_TABS = [
  { key: 'player', label: 'Player' },
  { key: 'fund', label: 'Managed fund' },
]

const sideLinkClass = ({ isActive }) => `side-link${isActive ? ' is-active' : ''}`

// A collapsible sidebar section: the header toggles its links open/closed. State is in-memory and defaults to
// open; the shell stays mounted across routes, so a collapsed section holds for the session.
function SideGroup({ title, children }) {
  const [open, setOpen] = useState(true)
  return (
    <div className={`side-group${open ? '' : ' is-collapsed'}`}>
      <button
        type="button"
        className="side-group-title"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
      >
        <span className="side-group-caret" aria-hidden="true">
          {open ? '▾' : '▸'}
        </span>
        {title}
      </button>
      {children}
    </div>
  )
}

// Total worth is coloured by the last completed cycle's change — green up, red down — and left ink-dark when
// flat or before the first cycle finishes; the returned tone drives both the colour and the glyph.
function worthToneOf(change) {
  if (typeof change !== 'number' || change === 0) return null
  return change > 0 ? 'up' : 'down'
}

// One labelled cash/worth delta in the sidebar wallet, coloured and glyphed by direction so the sign never
// rides on colour alone; a non-numeric value (before the first completed cycle) shows an em dash.
function SidebarChangeStat({ label, value }) {
  const tone = typeof value === 'number' ? toneOf(value) : 'flat'
  return (
    <div className="sidebar-stat">
      <span className="map-stat-label">{label}</span>
      <span className={`sidebar-stat-value num tone-${tone}`}>
        {typeof value === 'number' ? (
          <>
            <span aria-hidden="true">{CHANGE_GLYPH[tone]} </span>
            {formatSigned(value)}
          </>
        ) : (
          '—'
        )}
      </span>
    </div>
  )
}

// Layout route: the persistent left sidebar, the shared top navbar, and the routed page between them. The
// shell owns the market poll and the Start/Pause/Reset actions so the navbar can drive them on every page;
// pages read the market state and actions through the outlet context.
export function AppShell() {
  const location = useLocation()
  const [player, setPlayer] = useState(null)
  const [market, setMarket] = useState(null)
  const [connected, setConnected] = useState(false)
  const [ready, setReady] = useState(false)
  const [pending, setPending] = useState(false)
  const [actionError, setActionError] = useState(null)
  const [tradingClockSnapshot, setTradingClockSnapshot] = useState(null)
  const [clockNowMs, setClockNowMs] = useState(() => Date.now())
  // Whether the player trades and reads stats as themselves or through their managed fund; shared with the
  // dashboard tabs and the order book so one switch drives all three surfaces.
  const [actorKind, setActorKind] = useState('player')

  const load = useCallback(async () => {
    try {
      const { market: marketData, player: playerData } = await loadShellSnapshot(api)
      const receivedAtMs = Date.now()
      setMarket(marketData)
      setTradingClockSnapshot((previous) => {
        const next = createTradingClock(marketData, receivedAtMs)
        return shouldKeepTradingClock(previous, next) ? previous : next
      })
      setClockNowMs(receivedAtMs)
      setPlayer(playerData)
      setConnected(true)
    } catch {
      setConnected(false)
    } finally {
      setReady(true)
    }
  }, [])

  useEffect(() => {
    const initialId = setTimeout(load, 0)
    const intervalId = setInterval(load, SHELL_POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [load])

  useEffect(() => {
    if (tradingClockSnapshot?.marketStatus !== 'Running' || tradingClockSnapshot.remainingPhaseSeconds <= 0) {
      return undefined
    }

    const intervalId = setInterval(() => setClockNowMs(Date.now()), 1_000)
    return () => clearInterval(intervalId)
  }, [tradingClockSnapshot])

  const runAction = useCallback(
    async (action) => {
      setPending(true)
      setActionError(null)
      try {
        await action()
        await load()
      } catch (error) {
        setActionError(error.message)
      } finally {
        setPending(false)
      }
    },
    [load],
  )

  const resetMarket = useCallback(async () => {
    await api.resetMarket()
  }, [])

  const worthTone = player ? worthToneOf(player.lastCycleWorthChange) : null
  const marketActive = market?.status === 'Running'
  const tradingClock = formatTradingClock(interpolateTradingClock(tradingClockSnapshot, clockNowMs))
  const showFund = actorKind === 'fund'
  const hasFund = player?.fundParticipantId != null
  const needsPlayerOnboarding = ready && connected && market !== null && player === null
  const performanceStats = showFund
    ? [
        { key: 'cashLast', label: 'Cash · last cycle', value: player?.fundLastCycleMoneyChange },
      ]
    : [
        { key: 'cashLast', label: 'Cash · last cycle', value: player?.lastCycleMoneyChange },
      ]

  return (
    <div className={`app-shell${marketActive ? ' is-market-active' : ''}`}>
      <aside
        className="sidebar"
        inert={needsPlayerOnboarding ? true : undefined}
        aria-hidden={needsPlayerOnboarding ? true : undefined}
      >
        <nav className="sidebar-nav" aria-label="Primary">
          <NavLink className={sideLinkClass} to="/" end>
            Dashboard
          </NavLink>
          <NavLink className={sideLinkClass} to="/trade-market">
            Trade market
          </NavLink>
          <NavLink className={sideLinkClass} to="/player-stats">
            Player stats
          </NavLink>
          <NavLink className={sideLinkClass} to="/fund-stats">
            Fund stats
          </NavLink>

          <SideGroup title="Active market">
            <NavLink className={sideLinkClass} to="/traders">
              Traders
            </NavLink>
            <NavLink className={sideLinkClass} to="/companies">
              Companies
            </NavLink>
            <NavLink className={sideLinkClass} to="/industries">
              Industries
            </NavLink>
            <NavLink className={sideLinkClass} to="/news">
              News
            </NavLink>
            <NavLink className={sideLinkClass} to="/crises">
              Crises
            </NavLink>
            <NavLink className={sideLinkClass} to="/auditors">
              Auditors
            </NavLink>
            <NavLink className={sideLinkClass} to="/banks">
              Banks
            </NavLink>
            <NavLink className={sideLinkClass} to="/loans">
              Bank loans
            </NavLink>
          </SideGroup>

        </nav>
        {player ? (
          <div className="sidebar-player">
            <div className="actor-switch" role="group" aria-label="Trade as">
              {ACTOR_TABS.map((tab) => (
                <button
                  key={tab.key}
                  type="button"
                  className={`actor-switch-btn${actorKind === tab.key ? ' is-active' : ''}`}
                  aria-pressed={actorKind === tab.key}
                  onClick={() => setActorKind(tab.key)}
                >
                  {tab.label}
                </button>
              ))}
            </div>
            {showFund && !hasFund ? (
              <span className="map-stat-label">No fund yet — create one on the dashboard.</span>
            ) : showFund ? (
              <>
                <div className="sidebar-stat">
                  <span className="map-stat-label">Total worth</span>
                  <span className="sidebar-stat-value is-lead num">{formatMoney(player.fundTotalWorth)}</span>
                </div>
                <div className="sidebar-stat">
                  <span className="map-stat-label">Available</span>
                  <span className="sidebar-stat-value num">{formatMoney(player.fundAvailableBalance)}</span>
                </div>
                <div className="sidebar-stat">
                  <span className="map-stat-label">Holdings</span>
                  <span className="sidebar-stat-value num">{formatMoney(player.fundHoldingsValue)}</span>
                </div>
              </>
            ) : (
              <>
                <div className="sidebar-stat">
                  <span className="map-stat-label">Total worth</span>
                  <span className={`sidebar-stat-value is-lead num${worthTone ? ` tone-${worthTone}` : ''}`}>
                    {worthTone ? <span aria-hidden="true">{WORTH_GLYPH[worthTone]} </span> : null}
                    {formatMoney(player.totalWorth)}
                  </span>
                </div>
                <div className="sidebar-stat">
                  <span className="map-stat-label">Available</span>
                  <span className="sidebar-stat-value num">{formatMoney(player.availableBalance)}</span>
                </div>
                <div className="sidebar-stat">
                  <span className="map-stat-label">Shares bought</span>
                  <span className="sidebar-stat-value num">{formatInt(player.sharesOwned)}</span>
                </div>
              </>
            )}
            {!(showFund && !hasFund) ? (
              <div className="sidebar-perf">
                {performanceStats.map((stat) => (
                  <SidebarChangeStat key={stat.key} label={stat.label} value={stat.value} />
                ))}
              </div>
            ) : null}
          </div>
        ) : null}
      </aside>
      <div
        className="app"
        inert={needsPlayerOnboarding ? true : undefined}
        aria-hidden={needsPlayerOnboarding ? true : undefined}
      >
        <TopBar market={market} pending={pending} tradingClock={tradingClock} runAction={runAction} />
        {needsPlayerOnboarding && location.pathname !== '/' ? (
          <Navigate replace to="/" />
        ) : (
          <Outlet
            context={{ market, connected, ready, pending, actionError, runAction, resetMarket, player, actorKind, setActorKind }}
          />
        )}
      </div>
      {needsPlayerOnboarding ? <PlayerOnboarding onCreated={load} /> : null}
    </div>
  )
}
