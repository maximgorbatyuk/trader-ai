import { useCallback, useEffect, useState } from 'react'
import './App.css'
import { api } from './api'
import { formatInt } from './format'
import { Panel } from './Panel'
import { BankLoansTable } from './BankLoansTable'

const POLL_INTERVAL_MS = 2500
const PAGE_SIZE = 20

// Roster of bank loans. Bank and status filters, sort, and paging are held here and sent to the server.
function BankLoansPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [data, setData] = useState(null)
  const [banks, setBanks] = useState([])
  const [page, setPage] = useState(1)
  const [bankFilter, setBankFilter] = useState('')
  const [status, setStatus] = useState('active')
  const [sortKey, setSortKey] = useState('principal')
  const [sortDir, setSortDir] = useState('desc')

  // Banks seldom change, so the filter list is fetched once rather than on every poll.
  useEffect(() => {
    let active = true
    api
      .getBanks()
      .then((result) => {
        if (active) setBanks(result ?? [])
      })
      .catch(() => {
        // Leave the filter with just "All banks" if the list can't be loaded.
      })
    return () => {
      active = false
    }
  }, [])

  const loadAll = useCallback(async () => {
    try {
      const result = await api.getLoansPaged({
        page,
        pageSize: PAGE_SIZE,
        bankId: bankFilter || undefined,
        status,
        sort: sortKey,
        sortDir,
      })
      setData(result)
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [page, bankFilter, status, sortKey, sortDir])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll])

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
    <main className="main">
      {!ready ? (
        <section className="placeholder" aria-busy="true">
          <span className="spinner" aria-hidden="true" />
          <p>Loading loans…</p>
        </section>
      ) : (
        <>
          {loadError ? (
            <div className="banner" role="alert">
              <strong>Showing last known state.</strong>
              <span>{loadError}</span>
            </div>
          ) : null}

          <Panel title="Bank loans" count={`${formatInt(total)}`} className="panel-holdings">
            <div className="roster-toolbar">
              <select
                className="select select-sm"
                aria-label="Filter loans by bank"
                value={bankFilter}
                onChange={(event) => {
                  setBankFilter(event.target.value)
                  setPage(1)
                }}
              >
                <option value="">All banks</option>
                {banks.map((bank) => (
                  <option key={bank.id} value={bank.id}>
                    {bank.name}
                  </option>
                ))}
              </select>
              <select
                className="select select-sm"
                aria-label="Filter loans by status"
                value={status}
                onChange={(event) => {
                  setStatus(event.target.value)
                  setPage(1)
                }}
              >
                <option value="active">Active</option>
                <option value="closed">Closed</option>
                <option value="all">All</option>
              </select>
            </div>

            <BankLoansTable loans={items} sortKey={sortKey} sortDir={sortDir} onToggleSort={toggleSort} />

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

export default BankLoansPage
