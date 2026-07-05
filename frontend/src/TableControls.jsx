// Sortable numeric column header: shows the active sort direction with a glyph and reports clicks up. Mirrors
// the roster tables' header so the two share one look.
export function SortHeader({ label, columnKey, sortKey, sortDir, onToggle, title }) {
  const active = sortKey === columnKey
  return (
    <th scope="col" className="ta-r" aria-sort={active ? (sortDir === 'desc' ? 'descending' : 'ascending') : 'none'}>
      <button type="button" className={`th-sort ${active ? 'is-active' : ''}`} onClick={() => onToggle(columnKey)} title={title}>
        {label}
        <span className="th-sort-glyph" aria-hidden="true">
          {active ? (sortDir === 'desc' ? '▼' : '▲') : '↕'}
        </span>
      </button>
    </th>
  )
}

export function Pager({ page, pageCount, onPage }) {
  if (pageCount <= 1) return null
  return (
    <div className="pager">
      <button type="button" className="btn" disabled={page <= 1} onClick={() => onPage(page - 1)}>
        ← Prev
      </button>
      <span className="pager-status num">
        Page {page} / {pageCount}
      </span>
      <button type="button" className="btn" disabled={page >= pageCount} onClick={() => onPage(page + 1)}>
        Next →
      </button>
    </div>
  )
}
