import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from './api'
import { formatInt } from './format'

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

const STATUS_TONE = {
  Running: 'up',
  Paused: 'attention',
  Completed: 'muted',
  NotStarted: 'muted',
}

// Shared top navbar rendered by the app shell on every page: brand, market controls, and the
// cycle/status/connection badges. The market state and control actions are owned by the shell and passed in.
export function TopBar({ connected, ready, market, pending, runAction, resetMarket }) {
  return (
    <header className="topbar">
      <Link className="brand" to="/" aria-label="Trader AI dashboard">
        <span className="brand-mark" aria-hidden="true">
          TA
        </span>
        <span className="brand-name">Trader&nbsp;AI</span>
        <span className="brand-tag" aria-hidden="true">
          Market Simulator
        </span>
      </Link>
      <div className="topbar-status">
        {market ? (
          <Controls market={market} pending={pending} runAction={runAction} resetMarket={resetMarket} />
        ) : null}
        {market ? <CycleBadge cycleNumber={market.currentCycleNumber} /> : null}
        {market ? <StatusBadge status={market.status} /> : null}
        <ConnPill connected={connected} ready={ready} />
      </div>
    </header>
  )
}

function Controls({ market, pending, runAction, resetMarket }) {
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

  return (
    <div className="controls" role="group" aria-label="Market controls">
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

// Null until the market has run its first cycle, so the badge stays hidden rather than showing "#—".
function CycleBadge({ cycleNumber }) {
  if (cycleNumber == null) return null

  return (
    <span className="cycle-badge" role="status" aria-label={`Current cycle ${cycleNumber}`}>
      <span className="cycle-badge-label" aria-hidden="true">
        Cycle
      </span>
      <span className="cycle-badge-value">#{formatInt(cycleNumber)}</span>
    </span>
  )
}

function StatusBadge({ status }) {
  const tone = STATUS_TONE[status] ?? 'muted'
  return <span className={`pill pill-${tone}`}>{status}</span>
}

export function Footer() {
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
