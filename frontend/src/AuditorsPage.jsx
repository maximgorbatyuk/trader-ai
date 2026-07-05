import { useCallback, useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { Panel } from './Panel'
import { AuditorsTable } from './AuditorsTable'
import { AuditorDetail } from './AuditorDetail'

const POLL_INTERVAL_MS = 2500

// Roster of rating agencies with an in-page detail block. The selected auditor is held in the `?auditor=` query
// param so it survives refreshes and is deep-linkable; the table drives it.
function AuditorsPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [auditors, setAuditors] = useState([])
  const [searchParams, setSearchParams] = useSearchParams()

  const auditorParam = searchParams.get('auditor')
  const selectedId = auditorParam ? Number(auditorParam) : null

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

  function selectAuditor(auditorId) {
    setSearchParams({ auditor: String(auditorId) })
  }

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
            <AuditorsTable auditors={auditors} selectedId={selectedId} onSelectAuditor={selectAuditor} />
          </Panel>

          {selectedId ? (
            <AuditorDetail key={selectedId} auditorId={selectedId} />
          ) : (
            <p className="note traders-hint">Select an auditor above to see its details and audit history.</p>
          )}
        </>
      )}
    </main>
  )
}

export default AuditorsPage
