import { useCallback, useEffect, useState } from 'react'
import { useNavigate, useOutletContext } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt } from './format'
import { Panel } from './Panel'

const POLL_INTERVAL_MS = 2500

// Min/max sector drop across a crisis's industries, formatted as a signed range for the table.
function formatDropRange(crisis) {
  const percents = crisis.industries.map((link) => Number(link.impactPercent))
  if (percents.length === 0) return '—'
  const min = Math.min(...percents)
  const max = Math.max(...percents)
  return min === max ? `−${max.toFixed(1)}%` : `−${min.toFixed(1)}% to −${max.toFixed(1)}%`
}

// A crisis is active while the current cycle is within its duration window after the cycle it struck.
function isActive(crisis, currentCycleNumber) {
  if (typeof currentCycleNumber !== 'number') return false
  return (
    currentCycleNumber > crisis.triggeredInCycleNumber &&
    currentCycleNumber <= crisis.triggeredInCycleNumber + crisis.durationCycles
  )
}

// Roster of market crises, newest first. Each row opens the standalone crisis detail page with its full event
// timeline. The current cycle comes from the shell so an ongoing crisis can be flagged as still active.
function CrisesPage() {
  const { market } = useOutletContext()
  const currentCycleNumber = market?.currentCycleNumber ?? null
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [crises, setCrises] = useState([])
  const navigate = useNavigate()

  const loadAll = useCallback(async () => {
    try {
      const data = await api.getCrises(50)
      setCrises(data ?? [])
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

  return (
    <main className="main">
      {!ready ? (
        <section className="placeholder" aria-busy="true">
          <span className="spinner" aria-hidden="true" />
          <p>Loading crises…</p>
        </section>
      ) : (
        <>
          {loadError ? (
            <div className="banner" role="alert">
              <strong>Showing last known state.</strong>
              <span>{loadError}</span>
            </div>
          ) : null}

          <Panel title="Crises" count={`${formatInt(crises.length)}`} className="panel-holdings">
            {crises.length === 0 ? (
              <p className="note">No crisis has struck the market yet.</p>
            ) : (
              <div className="tbl-wrap">
                <table className="tbl">
                  <thead>
                    <tr>
                      <th scope="col">Crisis</th>
                      <th scope="col">Scope</th>
                      <th scope="col">Status</th>
                      <th scope="col" className="ta-r">
                        Sectors
                      </th>
                      <th scope="col" className="ta-r">
                        Drop
                      </th>
                      <th scope="col" className="ta-r">
                        Events
                      </th>
                      <th scope="col" className="ta-r">
                        Cycle
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {crises.map((crisis) => {
                      const active = isActive(crisis, currentCycleNumber)
                      return (
                        <tr key={crisis.id}>
                          <th scope="row" className="cell-ellipsis">
                            <button
                              type="button"
                              className="cell-name-btn cell-ellipsis"
                              onClick={() => navigate(`/crises/${crisis.id}`)}
                              title={`Open ${crisis.title}`}
                            >
                              {crisis.title}
                            </button>
                          </th>
                          <td>
                            <span className="tag">{crisis.scope}</span>
                          </td>
                          <td>
                            {active ? (
                              <span className="tag tag-rating-extra">Active</span>
                            ) : (
                              <span className="muted-sub">Ended</span>
                            )}
                          </td>
                          <td className="num ta-r">{formatInt(crisis.industries.length)}</td>
                          <td className="num ta-r tone-down">{formatDropRange(crisis)}</td>
                          <td className="num ta-r">{formatInt(crisis.eventCount)}</td>
                          <td className="num ta-r">{formatInt(crisis.triggeredInCycleNumber)}</td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </Panel>
        </>
      )}
    </main>
  )
}

export default CrisesPage
