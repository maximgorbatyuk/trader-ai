import { useCallback, useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { Panel } from './Panel'
import { CompaniesTable } from './CompaniesTable'
import { CompanyDetail } from './CompanyDetail'

const POLL_INTERVAL_MS = 2500

// Roster of companies with an in-page detail block. The selected company is held in the `?company=` query
// param so it survives refreshes and can be deep-linked from the dashboard CompanyModal; the table drives it.
function CompaniesPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [companies, setCompanies] = useState([])
  const [searchParams, setSearchParams] = useSearchParams()

  const companyParam = searchParams.get('company')
  const selectedId = companyParam ? Number(companyParam) : null

  const loadAll = useCallback(async () => {
    try {
      const data = await api.getCompanies()
      setCompanies(data ?? [])
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

  function selectCompany(companyId) {
    setSearchParams({ company: String(companyId) })
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
            Companies
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
            <p>Loading companies…</p>
          </section>
        ) : (
          <>
            {loadError ? (
              <div className="banner" role="alert">
                <strong>Showing last known state.</strong>
                <span>{loadError}</span>
              </div>
            ) : null}

            <Panel title="Companies" count={`${companies.length}`} className="panel-holdings">
              <CompaniesTable companies={companies} selectedId={selectedId} onSelectCompany={selectCompany} />
            </Panel>

            {selectedId ? (
              <CompanyDetail key={selectedId} companyId={selectedId} />
            ) : (
              <p className="note traders-hint">Select a company above to see full details and its price history.</p>
            )}
          </>
        )}
      </main>
    </div>
  )
}

export default CompaniesPage
