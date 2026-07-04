import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt } from './format'
import { Panel } from './Panel'
import { RatingBadge } from './RatingBadge'

const POLL_INTERVAL_MS = 2500
const PAGE_SIZE = 20

// The single-auditor block: identity plus a paginated table of the companies it has audited, newest first. Owns
// its own polling keyed on auditorId and page, and remounts (resetting the page) when the selected auditor
// changes because the parent keys it on the id.
export function AuditorDetail({ auditorId }) {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [auditor, setAuditor] = useState(null)
  const [audits, setAudits] = useState(null)
  const [page, setPage] = useState(1)

  const loadAll = useCallback(async () => {
    try {
      const [auditorData, auditData] = await Promise.all([
        api.getAuditor(auditorId),
        api.getAuditorAudits(auditorId, page, PAGE_SIZE),
      ])
      setAuditor(auditorData)
      setAudits(auditData)
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [auditorId, page])

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
      <section className="placeholder" aria-busy="true">
        <span className="spinner" aria-hidden="true" />
        <p>Loading auditor…</p>
      </section>
    )
  }

  if (auditor === null) {
    return (
      <div className="banner" role="alert">
        <strong>Couldn&apos;t load this auditor.</strong>
        <span>{loadError ?? 'Pick another auditor from the table.'}</span>
      </div>
    )
  }

  const total = audits?.total ?? 0
  const items = audits?.items ?? []
  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE))

  return (
    <section className="detail-stack" aria-label={`${auditor.name} details`}>
      {loadError ? (
        <div className="banner" role="alert">
          <strong>Showing last known state.</strong>
          <span>{loadError}</span>
        </div>
      ) : null}

      <section className="command" aria-label="Auditor identity">
        <div className="command-id">
          <span className="command-label">Auditor</span>
          <h2 className="command-name">{auditor.name}</h2>
        </div>
        <p className="note">{auditor.description}</p>
      </section>

      <Panel title="Audited companies" count={`${formatInt(total)}`} className="panel-holdings">
        {items.length === 0 ? (
          <p className="note">This auditor hasn&apos;t reviewed any company yet.</p>
        ) : (
          <>
            <div className="tbl-scroll">
              <table className="tbl">
                <thead>
                  <tr>
                    <th scope="col">Company</th>
                    <th scope="col">Result</th>
                    <th scope="col" className="ta-r">
                      Cycles ago
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {items.map((audit) => (
                    <tr key={audit.id}>
                      <th scope="row" className="cell-ellipsis">
                        <Link
                          className="cell-link"
                          to={`/companies?company=${audit.companyId}`}
                          title={`Open ${audit.companyName} in the Companies page`}
                        >
                          {audit.companyName}
                        </Link>
                      </th>
                      <td>
                        <RatingBadge rating={audit.rating} impactPercent={audit.impactPercent} />
                      </td>
                      <td className="num ta-r">{formatInt(audit.cyclesAgo)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {pageCount > 1 ? (
              <div className="pager">
                <button type="button" className="btn" disabled={page <= 1} onClick={() => setPage((value) => value - 1)}>
                  ← Prev
                </button>
                <span className="pager-status num">
                  Page {page} / {pageCount}
                </span>
                <button
                  type="button"
                  className="btn"
                  disabled={page >= pageCount}
                  onClick={() => setPage((value) => value + 1)}
                >
                  Next →
                </button>
              </div>
            ) : null}
          </>
        )}
      </Panel>
    </section>
  )
}
