import { useCallback, useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt } from './format'
import { Panel } from './Panel'
import { CompaniesTable } from './CompaniesTable'
import { CompanyDetail } from './CompanyDetail'

const POLL_INTERVAL_MS = 2500
const PAGE_SIZE = 20

// Roster of companies with an in-page detail block. Search, industry filter, sort, and paging are held here
// and sent to the server; the selected company stays in the `?company=` query param so it survives refreshes
// and can be deep-linked from the dashboard CompanyModal.
function CompaniesPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [data, setData] = useState(null)
  const [industries, setIndustries] = useState([])
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [industryFilter, setIndustryFilter] = useState('')
  const [sortKey, setSortKey] = useState('cost')
  const [sortDir, setSortDir] = useState('desc')
  const [searchParams, setSearchParams] = useSearchParams()

  const companyParam = searchParams.get('company')
  const selectedId = companyParam ? Number(companyParam) : null

  // Industries seldom change, so they are fetched once to populate the filter rather than on every poll.
  useEffect(() => {
    let active = true
    api
      .getIndustries()
      .then((data) => {
        if (active) setIndustries(data ?? [])
      })
      .catch(() => {
        // Leave the filter with just "All industries" if the list can't be loaded.
      })
    return () => {
      active = false
    }
  }, [])

  const loadAll = useCallback(async () => {
    try {
      const result = await api.getCompaniesPaged({
        page,
        pageSize: PAGE_SIZE,
        search,
        sort: sortKey,
        sortDir,
        industryId: industryFilter || undefined,
      })
      setData(result)
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [page, search, industryFilter, sortKey, sortDir])

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

  function toggleSort(key) {
    setPage(1)
    if (key === sortKey) {
      setSortDir((dir) => (dir === 'desc' ? 'asc' : 'desc'))
    } else {
      setSortKey(key)
      setSortDir('desc')
    }
  }

  const total = data?.total ?? 0
  const items = data?.items ?? []
  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE))

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

            <Panel title="Companies" count={`${formatInt(total)}`} className="panel-holdings">
              <div className="roster-toolbar">
                <input
                  className="select select-sm roster-search"
                  type="search"
                  placeholder="Search by name"
                  aria-label="Search companies by name"
                  value={search}
                  onChange={(event) => {
                    setSearch(event.target.value)
                    setPage(1)
                  }}
                />
                <select
                  className="select select-sm"
                  aria-label="Filter companies by industry"
                  value={industryFilter}
                  onChange={(event) => {
                    setIndustryFilter(event.target.value)
                    setPage(1)
                  }}
                >
                  <option value="">All industries</option>
                  {industries.map((industry) => (
                    <option key={industry.id} value={industry.id}>
                      {industry.name}
                    </option>
                  ))}
                </select>
              </div>

              <CompaniesTable
                companies={items}
                sortKey={sortKey}
                sortDir={sortDir}
                onToggleSort={toggleSort}
                selectedId={selectedId}
                onSelectCompany={selectCompany}
              />

              {pageCount > 1 ? (
                <div className="pager">
                  <button type="button" className="btn" disabled={page <= 1} onClick={() => setPage((value) => value - 1)}>
                    ← Prev
                  </button>
                  <span className="pager-status num">
                    Page {page} / {pageCount}
                  </span>
                  <button type="button" className="btn" disabled={page >= pageCount} onClick={() => setPage((value) => value + 1)}>
                    Next →
                  </button>
                </div>
              ) : null}
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
