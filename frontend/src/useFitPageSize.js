import { useCallback, useRef, useState } from 'react'

// Space below the list container that rows must not overlap: pager, panel padding, and the main bottom
// padding, plus a small safety margin. Kept conservative so the page never grows a vertical scrollbar.
const RESERVE_PX = 110
const MIN_ROWS = 6
const FALLBACK_ROW_HEIGHT = 33
// Rough chrome above the list (top bar, panel head, toolbar) used only for the pre-measurement estimate so
// the first fetch is close; the real measurement corrects it once the list mounts.
const ESTIMATED_CHROME_PX = 240

function estimateRows(reserve, min) {
  if (typeof window === 'undefined') return min
  const available = window.innerHeight - ESTIMATED_CHROME_PX - reserve
  return Math.max(min, Math.floor(available / FALLBACK_ROW_HEIGHT))
}

// Size a live list to the content area so it fills the viewport without a vertical scrollbar. Returns the
// computed page size and a callback ref for the list container; it measures the real container top, header,
// and row heights once the list mounts and re-measures on window resize.
export function useFitPageSize({ rowSelector = 'tbody tr', headerSelector = 'thead', reserve = RESERVE_PX, min = MIN_ROWS } = {}) {
  const [pageSize, setPageSize] = useState(() => estimateRows(reserve, min))
  const teardownRef = useRef(null)

  const containerRef = useCallback(
    (node) => {
      if (teardownRef.current) {
        teardownRef.current()
        teardownRef.current = null
      }
      if (!node) return

      const measure = () => {
        const top = node.getBoundingClientRect().top
        const header = headerSelector ? node.querySelector(headerSelector) : null
        const headerHeight = header ? header.getBoundingClientRect().height : 0
        const row = node.querySelector(rowSelector)
        const rowHeight = row ? row.getBoundingClientRect().height : FALLBACK_ROW_HEIGHT
        // Rows laid out in a flex/grid column are separated by a row-gap that a single row's box excludes; fold
        // it into the divisor so N rows plus their gaps fit. Table rows report `normal` (→ 0) and are unaffected.
        const rowGap = row ? parseFloat(getComputedStyle(row.parentElement).rowGap) || 0 : 0
        const available = window.innerHeight - top - headerHeight - reserve
        const next = Math.max(min, Math.floor((available + rowGap) / (rowHeight + rowGap)))
        setPageSize((prev) => (prev === next ? prev : next))
      }

      measure()
      window.addEventListener('resize', measure)
      teardownRef.current = () => window.removeEventListener('resize', measure)
    },
    [rowSelector, headerSelector, reserve, min],
  )

  return [pageSize, containerRef]
}
