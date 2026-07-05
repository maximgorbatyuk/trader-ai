import { useMemo, useState } from 'react'

// Client-side sort + pagination over an in-memory row set, for small per-player tables that reuse one array
// endpoint. Selecting a new column starts descending; re-selecting it flips direction. The page is clamped so
// it stays valid when the row set shrinks under a live poll.
export function useClientTable(rows, { pageSize = 20, initialSortKey = null, initialSortDir = 'desc' } = {}) {
  const [sortKey, setSortKey] = useState(initialSortKey)
  const [sortDir, setSortDir] = useState(initialSortDir)
  const [page, setPage] = useState(1)

  function toggleSort(key) {
    setPage(1)
    if (key === sortKey) {
      setSortDir((dir) => (dir === 'desc' ? 'asc' : 'desc'))
    } else {
      setSortKey(key)
      setSortDir('desc')
    }
  }

  const sorted = useMemo(() => {
    if (!sortKey) return rows
    const copy = [...rows]
    copy.sort((a, b) => {
      const av = a[sortKey]
      const bv = b[sortKey]
      let cmp
      if (typeof av === 'number' && typeof bv === 'number') cmp = av - bv
      else if (typeof av === 'boolean' && typeof bv === 'boolean') cmp = av === bv ? 0 : av ? 1 : -1
      else cmp = String(av ?? '').localeCompare(String(bv ?? ''))
      return sortDir === 'desc' ? -cmp : cmp
    })
    return copy
  }, [rows, sortKey, sortDir])

  const pageCount = Math.max(1, Math.ceil(sorted.length / pageSize))
  const safePage = Math.min(page, pageCount)
  const pageRows = sorted.slice((safePage - 1) * pageSize, safePage * pageSize)

  return { pageRows, sortKey, sortDir, toggleSort, page: safePage, pageCount, setPage }
}
