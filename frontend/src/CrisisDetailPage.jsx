import { useCallback, useEffect, useState } from 'react'
import { Link, useOutletContext, useParams } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt } from './format'
import { Panel } from './Panel'

const POLL_INTERVAL_MS = 2500

const EVENT_TYPE_LABEL = {
  IndustryShock: 'Sector shock',
  AuditorRating: 'Audit',
  Bankruptcy: 'Bankruptcy',
}

function isActive(crisis, currentCycleNumber) {
  if (typeof currentCycleNumber !== 'number' || !crisis) return false
  return (
    currentCycleNumber > crisis.triggeredInCycleNumber &&
    currentCycleNumber <= crisis.triggeredInCycleNumber + crisis.durationCycles
  )
}

function formatImpact(percent) {
  return typeof percent === 'number' ? `−${percent.toFixed(1)}%` : '—'
}

// Full-page crisis detail, reached from the Crises roster and the dashboard crisis banner. Shows the crisis
// header, the sectors it shocked, and a single timeline of everything recorded against it while active — the
// trigger shocks, auditor High/Extra verdicts, and trader bankruptcies. Owns its own polling keyed on the id.
function CrisisDetailPage() {
  const { id } = useParams()
  const crisisId = Number(id)
  const { market } = useOutletContext()
  const currentCycleNumber = market?.currentCycleNumber ?? null
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [crisis, setCrisis] = useState(null)

  const loadAll = useCallback(async () => {
    try {
      const data = await api.getCrisis(crisisId)
      setCrisis(data)
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [crisisId])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll])

  if (!ready) {
    return (
      <main className="main">
        <section className="placeholder" aria-busy="true">
          <span className="spinner" aria-hidden="true" />
          <p>Loading crisis…</p>
        </section>
      </main>
    )
  }

  if (crisis === null) {
    return (
      <main className="main">
        <div className="banner" role="alert">
          <strong>Couldn&apos;t load this crisis.</strong>
          <span>{loadError ?? 'Pick another crisis from the Crises page.'}</span>
        </div>
      </main>
    )
  }

  const active = isActive(crisis, currentCycleNumber)
  const endsAtCycle = crisis.triggeredInCycleNumber + crisis.durationCycles

  return (
    <main className="main">
      <section className="detail-stack" aria-label={`${crisis.title} details`}>
        {loadError ? (
          <div className="banner" role="alert">
            <strong>Showing last known state.</strong>
            <span>{loadError}</span>
          </div>
        ) : null}

        <section className="command" aria-label="Crisis identity">
          <div className="command-id">
            <span className="command-label">
              {crisis.scope} crisis{' '}
              {active ? (
                <span className="tag tag-rating-extra">Active</span>
              ) : (
                <span className="muted-sub">Ended</span>
              )}
            </span>
            <h2 className="command-name">{crisis.title}</h2>
          </div>
          <p className="note">{crisis.content}</p>
          <p className="note num">
            Struck cycle {formatInt(crisis.triggeredInCycleNumber)} · runs {formatInt(crisis.durationCycles)}{' '}
            cycles through cycle {formatInt(endsAtCycle)}
          </p>
        </section>

        <Panel title="Shocked sectors" count={`${formatInt(crisis.industries.length)}`} className="panel-holdings">
          {crisis.industries.length === 0 ? (
            <p className="note">No sectors recorded.</p>
          ) : (
            <div className="tbl-scroll">
              <table className="tbl">
                <thead>
                  <tr>
                    <th scope="col">Sector</th>
                    <th scope="col" className="ta-r">
                      Drop
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {crisis.industries.map((link) => (
                    <tr key={link.industryId}>
                      <th scope="row" className="cell-ellipsis">
                        {link.industryName}
                      </th>
                      <td className="num ta-r tone-down">{formatImpact(Number(link.impactPercent))}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Panel>

        <Panel title="Timeline" count={`${formatInt(crisis.events.length)}`} className="panel-holdings">
          {crisis.events.length === 0 ? (
            <p className="note">Nothing else happened during this crisis.</p>
          ) : (
            <div className="tbl-scroll">
              <table className="tbl">
                <thead>
                  <tr>
                    <th scope="col" className="ta-r">
                      Cycle
                    </th>
                    <th scope="col">Type</th>
                    <th scope="col">What happened</th>
                    <th scope="col" className="ta-r">
                      Impact
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {crisis.events.map((event) => (
                    <tr key={event.id}>
                      <td className="num ta-r">{formatInt(event.createdInCycleNumber)}</td>
                      <td>
                        <span className="tag">{EVENT_TYPE_LABEL[event.type] ?? event.type}</span>
                      </td>
                      <td className="cell-ellipsis">
                        {event.companyId != null ? (
                          <Link className="cell-link" to={`/companies/${event.companyId}`}>
                            {event.companyName ?? event.description}
                          </Link>
                        ) : (
                          event.description
                        )}
                      </td>
                      <td className="num ta-r tone-down">
                        {event.impactPercent != null ? formatImpact(Number(event.impactPercent)) : '—'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Panel>
      </section>
    </main>
  )
}

export default CrisisDetailPage
