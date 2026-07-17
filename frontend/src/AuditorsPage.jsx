import { useCallback, useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { Panel } from './Panel'
import { AuditorsTable } from './AuditorsTable'
import { useFitPageSize } from './useFitPageSize'

const POLL_INTERVAL_MS = 2500

// Roster of rating agencies; each row opens the standalone auditor detail page with its full audit history.
function AuditorsPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [auditors, setAuditors] = useState([])
  const [page, setPage] = useState(1)
  const [pageSize, tableRef] = useFitPageSize()
  const navigate = useNavigate()

  // A resize can shrink the page size (or a live refresh the roster) below the stored page, so the effective
  // page is clamped on render rather than stored.
  const pageCount = Math.max(1, Math.ceil(auditors.length / pageSize))
  const currentPage = Math.min(page, pageCount)
  const pageItems = auditors.slice((currentPage - 1) * pageSize, currentPage * pageSize)

  const loadAll = useCallback(async () => {
    try {
      const data = await api.getAuditors()
      setAuditors(data ?? [])
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
          <p>Loading auditors…</p>
        </section>
      ) : (
        <>
          {loadError ? (
            <div className="banner" role="alert">
              <strong>Showing last known state.</strong>
              <span>{loadError}</span>
            </div>
          ) : null}

          <Panel title="Auditors" count={`${auditors.length}`} className="panel-holdings">
            <div ref={tableRef}>
              <AuditorsTable auditors={pageItems} onSelectAuditor={(auditorId) => navigate(`/auditors/${auditorId}`)} />
            </div>

            {pageCount > 1 ? (
              <div className="pager">
                <button type="button" className="btn" disabled={currentPage <= 1} onClick={() => setPage(currentPage - 1)}>
                  ← Prev
                </button>
                <span className="pager-status num">
                  Page {currentPage} / {pageCount}
                </span>
                <button type="button" className="btn" disabled={currentPage >= pageCount} onClick={() => setPage(currentPage + 1)}>
                  Next →
                </button>
              </div>
            ) : null}
          </Panel>
        </>
      )}
    </main>
  )
}

export default AuditorsPage
