import { useCallback, useEffect, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt } from './format'
import { Panel } from './Panel'
import { CompaniesTable } from './CompaniesTable'
import ClosedCompaniesView from './ClosedCompaniesPage'

const POLL_INTERVAL_MS = 2500
const PAGE_SIZE = 20

// Roster of companies. Search, industry filter, sort, and paging are held here and sent to the server;
// clicking a company opens its own detail page.
function CompaniesPage() {
  const navigate = useNavigate()
  const [searchParams, setSearchParams] = useSearchParams()
  const view = searchParams.get('view') === 'closed' ? 'closed' : 'active'
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [data, setData] = useState(null)
  const [industries, setIndustries] = useState([])
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [industryFilter, setIndustryFilter] = useState('')
  const [sortKey, setSortKey] = useState('cost')
  const [sortDir, setSortDir] = useState('desc')

  // Industries seldom change, so they are fetched once to populate the filter rather than on every poll.
  useEffect(() => {
    if (view !== 'active') return undefined

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
  }, [view])

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
    if (view !== 'active') return undefined

    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll, view])

  function selectCompany(companyId) {
    navigate(`/companies/${companyId}`)
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
  const statusControl = (
    <select
      className="select select-sm"
      aria-label="Filter companies by status"
      value={view}
      onChange={(event) => setSearchParams({ view: event.target.value })}
    >
      <option value="active">Active companies</option>
      <option value="closed">Closed companies</option>
    </select>
  )

  return (
    <main className="main">
      {view === 'closed' ? (
        <ClosedCompaniesView statusControl={statusControl} />
      ) : !ready ? (
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
              {statusControl}
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
        </>
      )}
    </main>
  )
}

export default CompaniesPage
