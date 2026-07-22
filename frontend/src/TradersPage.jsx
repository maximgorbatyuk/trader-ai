import { useCallback, useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt } from './format'
import { Panel } from './Panel'
import { TradersTable } from './TradersTable'
import ClosedFundsView from './ClosedFundsPage'
import DepartedTradersView from './DepartedTradersPage'
import { useFitPageSize } from './useFitPageSize'

const POLL_INTERVAL_MS = 2500

// Views served by the paged roster table; the remaining views delegate to their own sibling pages.
const ROSTER_VIEWS = ['all', 'active', 'in-fund']

const TYPE_OPTIONS = [
  { value: 'all', label: 'All types' },
  { value: 'AIAgent', label: 'AI' },
  { value: 'Individual', label: 'Individual' },
  { value: 'Player', label: 'Player' },
  { value: 'CollectiveFund', label: 'Fund' },
]

// Roster of traders. Search, type filter, sort, and paging are held here and sent to the server; clicking a
// trader opens its own detail page.
function TradersPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const requestedView = searchParams.get('view')
  const view =
    requestedView === 'departed' || requestedView === 'closed-funds' || ROSTER_VIEWS.includes(requestedView)
      ? requestedView
      : 'all'
  const rosterStatus = view === 'active' ? 'active' : view === 'in-fund' ? 'in-fund' : undefined
  // The page lives in the URL so a paged position survives navigating into a trader and back, and Prev/Next
  // become history entries the browser buttons can walk.
  const page = Math.max(1, Number.parseInt(searchParams.get('page'), 10) || 1)
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [data, setData] = useState(null)
  const [search, setSearch] = useState('')
  const [typeFilter, setTypeFilter] = useState('all')
  const [sortKey, setSortKey] = useState('total')
  const [sortDir, setSortDir] = useState('desc')
  const [pageSize, tableRef] = useFitPageSize()

  // Write the page into the query param while preserving the current view. Explicit paging pushes a history
  // entry; filter/sort resets and the live-poll clamp pass { replace: true } so they do not pollute history.
  const setPage = useCallback(
    (next, options) => {
      setSearchParams((prev) => {
        const params = new URLSearchParams(prev)
        if (next <= 1) params.delete('page')
        else params.set('page', String(next))
        return params
      }, options)
    },
    [setSearchParams],
  )

  const loadAll = useCallback(async () => {
    try {
      const result = await api.getParticipantsPaged({
        page,
        pageSize,
        search,
        sort: sortKey,
        sortDir,
        type: typeFilter === 'all' ? undefined : typeFilter,
        status: rosterStatus,
      })
      // A resize can shrink the page size (or a live refresh the total) below the current page; snap back.
      const resultPageCount = Math.max(1, Math.ceil((result?.total ?? 0) / pageSize))
      if (page > resultPageCount) {
        setPage(resultPageCount, { replace: true })
        return
      }
      setData(result)
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [page, pageSize, search, typeFilter, sortKey, sortDir, rosterStatus, setPage])

  useEffect(() => {
    if (!ROSTER_VIEWS.includes(view)) return undefined

    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll, view])

  function toggleSort(key) {
    setPage(1, { replace: true })
    if (key === sortKey) {
      setSortDir((dir) => (dir === 'desc' ? 'asc' : 'desc'))
    } else {
      setSortKey(key)
      setSortDir('desc')
    }
  }

  const total = data?.total ?? 0
  const items = data?.items ?? []
  const pageCount = Math.max(1, Math.ceil(total / pageSize))
  const statusControl = (
    <select
      className="select select-sm"
      aria-label="Filter traders by status"
      value={view}
      onChange={(event) => setSearchParams({ view: event.target.value })}
    >
      <option value="all">All traders</option>
      <option value="active">Active traders</option>
      <option value="in-fund">Joined to a fund</option>
      <option value="departed">Departed traders</option>
      <option value="closed-funds">Closed funds</option>
    </select>
  )

  return (
    <main className="main">
      {view === 'departed' ? (
        <DepartedTradersView statusControl={statusControl} />
      ) : view === 'closed-funds' ? (
        <ClosedFundsView statusControl={statusControl} />
      ) : !ready ? (
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

          <Panel title="Traders" count={`${formatInt(total)}`} className="panel-holdings">
            <div className="roster-toolbar">
              {statusControl}
              <input
                className="select select-sm roster-search"
                type="search"
                placeholder="Search by name"
                aria-label="Search traders by name"
                value={search}
                onChange={(event) => {
                  setSearch(event.target.value)
                  setPage(1, { replace: true })
                }}
              />
              <select
                className="select select-sm"
                aria-label="Filter traders by type"
                value={typeFilter}
                onChange={(event) => {
                  setTypeFilter(event.target.value)
                  setPage(1, { replace: true })
                }}
              >
                {TYPE_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            </div>

            <div ref={tableRef}>
              <TradersTable
                participants={items}
                sortKey={sortKey}
                sortDir={sortDir}
                onToggleSort={toggleSort}
              />
            </div>

            {pageCount > 1 ? (
              <div className="pager">
                <button type="button" className="btn" disabled={page <= 1} onClick={() => setPage(page - 1)}>
                  ← Prev
                </button>
                <span className="pager-status num">
                  Page {page} / {pageCount}
                </span>
                <button type="button" className="btn" disabled={page >= pageCount} onClick={() => setPage(page + 1)}>
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

export default TradersPage
