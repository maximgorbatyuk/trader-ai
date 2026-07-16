import { Link, NavLink } from 'react-router-dom'
import { api } from './api'

const STATUS_TONE = {
  Running: 'up',
  Paused: 'attention',
  Completed: 'muted',
  NotStarted: 'muted',
}

// Shared top navbar rendered by the app shell on every page: brand, the Start/Pause trading control, the
// trading-clock and status badges, and the About and Settings links. Market state and the run action are
// owned by the shell and passed in.
export function TopBar({ market, pending, tradingClock, runAction }) {
  return (
    <header className="topbar">
      <Link className="brand" to="/" aria-label="Trader AI dashboard">
        <span className="brand-mark" aria-hidden="true">
          TA
        </span>
        <span className="brand-name">Trader&nbsp;AI</span>
      </Link>
      <div className="topbar-status">
        {market ? <Controls market={market} pending={pending} runAction={runAction} /> : null}
        {tradingClock ? <TradingClockBadges clock={tradingClock} /> : null}
        {market ? <StatusBadge status={market.status} /> : null}
        <NavLink
          className={({ isActive }) => `topbar-icon-link${isActive ? ' is-active' : ''}`}
          to="/about"
          aria-label="About"
          title="About"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <circle cx="12" cy="12" r="10" />
            <path d="M12 16v-4" />
            <path d="M12 8h.01" />
          </svg>
        </NavLink>
        <NavLink
          className={({ isActive }) => `topbar-icon-link${isActive ? ' is-active' : ''}`}
          to="/settings"
          aria-label="Settings"
          title="Settings"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.38a2 2 0 0 0-.73-2.73l-.15-.09a2 2 0 0 1-1-1.74v-.51a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2Z" />
            <circle cx="12" cy="12" r="3" />
          </svg>
        </NavLink>
      </div>
    </header>
  )
}

function Controls({ market, pending, runAction }) {
  const running = market.status === 'Running'

  return (
    <div className="controls" role="group" aria-label="Market controls">
      {running ? (
        <button className="btn btn-primary" disabled={pending} onClick={() => runAction(api.pauseMarket)}>
          Pause trading
        </button>
      ) : (
        <button className="btn btn-primary" disabled={pending} onClick={() => runAction(api.startMarket)}>
          Start trading
        </button>
      )}
    </div>
  )
}

function TradingClockBadges({ clock }) {
  return (
    <span
      className="topbar-status"
      role="group"
      aria-label={`${clock.dayPhaseLabel}; ${clock.cycleLabel}; ${clock.timeLabel}`}
    >
      {[clock.dayPhaseLabel, clock.cycleLabel, clock.timeLabel].map((label) => (
        <span className="cycle-badge" key={label}>
          <span className="cycle-badge-value">{label}</span>
        </span>
      ))}
    </span>
  )
}

function StatusBadge({ status }) {
  const tone = STATUS_TONE[status] ?? 'muted'
  return <span className={`pill pill-${tone}`}>{status}</span>
}
