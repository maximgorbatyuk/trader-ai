import { useCallback, useEffect, useState } from 'react'
import { NavLink, Outlet } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt, formatMoney } from './format'
import { Footer, TopBar } from './Chrome'

const SHELL_POLL_INTERVAL_MS = 1500
const WORTH_GLYPH = { up: '▲', down: '▼' }

// Total worth is coloured by the last completed cycle's change — green up, red down — and left ink-dark when
// flat or before the first cycle finishes; the returned tone drives both the colour and the glyph.
function worthToneOf(change) {
  if (typeof change !== 'number' || change === 0) return null
  return change > 0 ? 'up' : 'down'
}

// Layout route: the persistent left sidebar, the shared top navbar and footer, and the routed page between
// them. The shell owns the market poll and the Step/Start/Pause/Reset actions so the navbar can drive them on
// every page; pages read the market state and actions through the outlet context.
export function AppShell() {
  const [player, setPlayer] = useState(null)
  const [market, setMarket] = useState(null)
  const [connected, setConnected] = useState(false)
  const [ready, setReady] = useState(false)
  const [pending, setPending] = useState(false)
  const [actionError, setActionError] = useState(null)

  const load = useCallback(async () => {
    try {
      const [marketData, playerData] = await Promise.all([api.getMarket(), api.getPlayer()])
      setMarket(marketData)
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

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <nav className="sidebar-nav" aria-label="Primary">
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/" end>
            Dashboard
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/trade-market">
            Trade market
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/traders">
            Traders
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/companies">
            Companies
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/closed-companies">
            Closed companies
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/industries">
            Industries
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/news">
            News
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/auditors">
            Auditors
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/departed-traders">
            Departed traders
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/closed-funds">
            Closed funds
          </NavLink>
        </nav>
        {player ? (
          <div className="sidebar-player">
            <span className="sidebar-player-title">Player</span>
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
          </div>
        ) : null}
      </aside>
      <div className="app">
        <TopBar
          connected={connected}
          ready={ready}
          market={market}
          pending={pending}
          runAction={runAction}
          resetMarket={resetMarket}
        />
        <Outlet context={{ market, connected, ready, pending, actionError, runAction }} />
        <Footer />
      </div>
    </div>
  )
}
