import { Link, useOutletContext } from 'react-router-dom'
import './App.css'
import { ParticipantDetail } from './ParticipantDetail'

// The shell already keeps the player and managed-fund identity current, so this page only selects the fund's
// participant detail instead of starting another polling loop.
function FundStatsPage() {
  const { player, ready } = useOutletContext()

  if (!ready) {
    return (
      <main className="main">
        <section className="placeholder" aria-busy="true">
          <span className="spinner" aria-hidden="true" />
          <p>Loading the managed fund…</p>
        </section>
      </main>
    )
  }

  if (!player) {
    return (
      <main className="main">
        <div className="banner" role="alert">
          <strong>No player has joined.</strong>
          <span>
            Join the market from the{' '}
            <Link className="cell-link" to="/">
              dashboard
            </Link>{' '}
            before creating a managed fund.
          </span>
        </div>
      </main>
    )
  }

  if (player.fundParticipantId == null) {
    return (
      <main className="main">
        <div className="banner" role="status">
          <strong>No managed fund yet.</strong>
          <span>
            Create one from the{' '}
            <Link className="cell-link" to="/">
              dashboard
            </Link>{' '}
            to see its stats here.
          </span>
        </div>
      </main>
    )
  }

  return (
    <main className="main">
      <ParticipantDetail key={player.fundParticipantId} participantId={player.fundParticipantId} showFavoriteCompanies />
    </main>
  )
}

export default FundStatsPage
