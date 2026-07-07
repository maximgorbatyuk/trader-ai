import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { ParticipantDetail } from './ParticipantDetail'

const POLL_INTERVAL_MS = 2500

// Full-page detail for the human player's own participant, reached from the sidebar. It resolves the player id
// (polling so the page fills in once someone joins) and then reuses ParticipantDetail, which owns the rest of
// the polling keyed on that id.
function PlayerStatsPage() {
  const [ready, setReady] = useState(false)
  const [playerId, setPlayerId] = useState(null)

  const load = useCallback(async () => {
    try {
      const player = await api.getPlayer()
      setPlayerId(player ? player.id : null)
    } catch {
      // Keep the last known id when a refresh fails.
    } finally {
      setReady(true)
    }
  }, [])

  useEffect(() => {
    const initialId = setTimeout(load, 0)
    const intervalId = setInterval(load, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [load])

  if (!ready) {
    return (
      <main className="main">
        <section className="placeholder" aria-busy="true">
          <span className="spinner" aria-hidden="true" />
          <p>Loading the player…</p>
        </section>
      </main>
    )
  }

  if (playerId === null) {
    return (
      <main className="main">
        <div className="banner" role="alert">
          <strong>No player has joined.</strong>
          <span>
            Join the market from the{' '}
            <Link className="cell-link" to="/">
              dashboard
            </Link>{' '}
            to see your stats here.
          </span>
        </div>
      </main>
    )
  }

  return (
    <main className="main">
      <ParticipantDetail key={playerId} participantId={playerId} />
    </main>
  )
}

export default PlayerStatsPage
