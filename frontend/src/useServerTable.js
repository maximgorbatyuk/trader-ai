import { useCallback, useEffect, useRef, useState } from 'react'

// Server-side sort + pagination for a live table. The fetcher returns one page keyed by
// (page, pageSize, sort, sortDir); the hook polls on an interval, clamps the page when the total
// shrinks under a live refresh, and drops out-of-order responses so a slow request can't overwrite a
// newer one. Returns the same shape as useClientTable so SortHeader and Pager work unchanged. The
// fetcher must be stable (wrap it in useCallback) or the poll effect re-subscribes every render.
export function useServerTable(fetcher, { pageSize = 10, initialSortKey = null, initialSortDir = 'desc', pollMs = 2500 } = {}) {
  const [sortKey, setSortKey] = useState(initialSortKey)
  const [sortDir, setSortDir] = useState(initialSortDir)
  const [page, setPage] = useState(1)
  const [data, setData] = useState(null)
  const [ready, setReady] = useState(false)
  const [error, setError] = useState(null)
  const requestSequence = useRef(0)

  // Selecting a new column starts descending; re-selecting it flips direction — same rule as useClientTable.
  function toggleSort(key) {
    setPage(1)
    if (key === sortKey) {
      setSortDir((dir) => (dir === 'desc' ? 'asc' : 'desc'))
    } else {
      setSortKey(key)
      setSortDir('desc')
    }
  }

  const load = useCallback(async () => {
    const requestId = ++requestSequence.current
    try {
      const result = await fetcher(page, pageSize, sortKey, sortDir)
      if (requestId !== requestSequence.current) return

      const resultPageCount = Math.max(1, Math.ceil((result?.total ?? 0) / (result?.pageSize || pageSize)))
      if (page > resultPageCount) {
        setPage(resultPageCount)
        return
      }

      setData(result)
      setError(null)
    } catch (err) {
      if (requestId !== requestSequence.current) return
      setError(err.message)
    } finally {
      if (requestId === requestSequence.current) setReady(true)
    }
  }, [fetcher, page, pageSize, sortKey, sortDir])

  useEffect(() => {
    const initialId = setTimeout(load, 0)
    const intervalId = setInterval(load, pollMs)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
      requestSequence.current += 1
    }
  }, [load, pollMs])

  const items = data?.items ?? []
  const total = data?.total ?? 0
  const pageCount = Math.max(1, Math.ceil(total / (data?.pageSize || pageSize)))
  const displayedPage = data?.page ?? page

  return { items, total, page: displayedPage, pageCount, setPage, sortKey, sortDir, toggleSort, ready, error }
}
