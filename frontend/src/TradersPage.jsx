import { useCallback, useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { Panel } from './Panel'
import { TradersTable } from './TradersTable'
import { ParticipantDetail } from './ParticipantDetail'

const POLL_INTERVAL_MS = 2500

// Roster of traders with an in-page detail block. The selected trader is held in the `?trader=` query param
// so it survives refreshes and can be deep-linked from the dashboard summary modal; the table drives it.
function TradersPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [participants, setParticipants] = useState([])
  const [searchParams, setSearchParams] = useSearchParams()

  const traderParam = searchParams.get('trader')
  const selectedId = traderParam ? Number(traderParam) : null

  const loadAll = useCallback(async () => {
    try {
      const data = await api.getParticipants()
      setParticipants(data ?? [])
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll])

  function selectTrader(participant) {
    setSearchParams({ trader: String(participant.id) })
  }

  return (
    <div className="app">
      <header className="topbar">
        <Link className="brand" to="/" aria-label="Back to the Trader AI dashboard">
          <span className="brand-mark" aria-hidden="true">
            TA
          </span>
          <span className="brand-name">Trader&nbsp;AI</span>
          <span className="brand-tag" aria-hidden="true">
            Traders
          </span>
        </Link>
        <Link className="btn" to="/">
          ← Dashboard
        </Link>
      </header>

      <main className="main">
        {!ready ? (
          <section className="placeholder" aria-busy="true">
            <span className="spinner" aria-hidden="true" />
            <p>Loading traders…</p>
          </section>
        ) : (
          <>
            {loadError ? (
              <div className="banner" role="alert">
                <strong>Showing last known state.</strong>
                <span>{loadError}</span>
              </div>
            ) : null}

            <Panel title="Traders" count={`${participants.length}`} className="panel-holdings">
              <TradersTable participants={participants} selectedId={selectedId} onSelectTrader={selectTrader} />
            </Panel>

            {selectedId ? (
              <ParticipantDetail key={selectedId} participantId={selectedId} />
            ) : (
              <p className="note traders-hint">Select a trader above to see full details and their total-worth history.</p>
            )}
          </>
        )}
      </main>
    </div>
  )
}

export default TradersPage
