import { useCallback, useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt } from './format'
import { Panel } from './Panel'
import { TradersTable } from './TradersTable'
import { ParticipantDetail } from './ParticipantDetail'

const POLL_INTERVAL_MS = 2500
const PAGE_SIZE = 20

const TYPE_OPTIONS = [
  { value: 'all', label: 'All types' },
  { value: 'AIAgent', label: 'AI' },
  { value: 'Individual', label: 'Individual' },
  { value: 'Player', label: 'Player' },
  { value: 'CollectiveFund', label: 'Fund' },
]

// Roster of traders with an in-page detail block. Search, type filter, sort, and paging are held here and
// sent to the server; the selected trader stays in the `?trader=` query param so it survives refreshes and
// can be deep-linked from the dashboard summary modal.
function TradersPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [data, setData] = useState(null)
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [typeFilter, setTypeFilter] = useState('all')
  const [sortKey, setSortKey] = useState('total')
  const [sortDir, setSortDir] = useState('desc')
  const [searchParams, setSearchParams] = useSearchParams()

  const traderParam = searchParams.get('trader')
  const selectedId = traderParam ? Number(traderParam) : null

  const loadAll = useCallback(async () => {
    try {
      const result = await api.getParticipantsPaged({
        page,
        pageSize: PAGE_SIZE,
        search,
        sort: sortKey,
        sortDir,
        type: typeFilter === 'all' ? undefined : typeFilter,
      })
      setData(result)
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [page, search, typeFilter, sortKey, sortDir])

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

            <Panel title="Traders" count={`${formatInt(total)}`} className="panel-holdings">
              <div className="roster-toolbar">
                <input
                  className="select select-sm roster-search"
                  type="search"
                  placeholder="Search by name"
                  aria-label="Search traders by name"
                  value={search}
                  onChange={(event) => {
                    setSearch(event.target.value)
                    setPage(1)
                  }}
                />
                <select
                  className="select select-sm"
                  aria-label="Filter traders by type"
                  value={typeFilter}
                  onChange={(event) => {
                    setTypeFilter(event.target.value)
                    setPage(1)
                  }}
                >
                  {TYPE_OPTIONS.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </select>
              </div>

              <TradersTable
                participants={items}
                sortKey={sortKey}
                sortDir={sortDir}
                onToggleSort={toggleSort}
                selectedId={selectedId}
                onSelectTrader={selectTrader}
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
