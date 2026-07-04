import { useEffect, useState } from 'react'
import { NavLink, Outlet } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt, formatMoney } from './format'

const PLAYER_POLL_INTERVAL_MS = 2500
const WORTH_GLYPH = { up: '▲', down: '▼' }

// Total worth is coloured by the last completed cycle's change — green up, red down — and left ink-dark when
// flat or before the first cycle finishes; the returned tone drives both the colour and the glyph.
function worthToneOf(change) {
  if (typeof change !== 'number' || change === 0) return null
  return change > 0 ? 'up' : 'down'
}

// Layout route: the persistent left sidebar plus the routed page. Every page nests under it so the sidebar
// stays put while the content area swaps; each page still owns its own topbar and polling.
export function AppShell() {
  const [player, setPlayer] = useState(null)

  useEffect(() => {
    async function load() {
      try {
        setPlayer(await api.getPlayer())
      } catch {
        // Keep the last known stats when a refresh fails.
      }
    }

    const initialId = setTimeout(load, 0)
    const intervalId = setInterval(load, PLAYER_POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [])

  const worthTone = player ? worthToneOf(player.lastCycleWorthChange) : null

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <nav className="sidebar-nav" aria-label="Primary">
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/" end>
            Dashboard
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/traders">
            Traders
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/companies">
            Companies
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/auditors">
            Auditors
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/departed-traders">
            Departed traders
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
      <Outlet />
    </div>
  )
}
