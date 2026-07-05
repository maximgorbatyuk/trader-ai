import { formatInt, formatMoney } from './format'
import { RatingBadge } from './RatingBadge'

// Presentational roster of one page of companies. Sorting, searching, industry filtering, and paging are
// owned by the Companies page (the server does the work); this component renders the current page and reports
// header clicks up via onToggleSort. The name is a button so the page can open the detail block below it.
export function CompaniesTable({ companies, sortKey, sortDir, onToggleSort, onSelectCompany, selectedId }) {
  function sortableHeader(key, label, title) {
    const active = sortKey === key
    return (
      <th scope="col" className="ta-r" aria-sort={active ? (sortDir === 'desc' ? 'descending' : 'ascending') : 'none'}>
        <button
          type="button"
          className={`th-sort ${active ? 'is-active' : ''}`}
          onClick={() => onToggleSort(key)}
          title={title}
        >
          {label}
          <span className="th-sort-glyph" aria-hidden="true">
            {active ? (sortDir === 'desc' ? '▼' : '▲') : '↕'}
          </span>
        </button>
      </th>
    )
  }

  if (companies.length === 0) {
    return <p className="note">No companies match the current search and filter.</p>
  }

  return (
    <div className="tbl-scroll">
      <table className="tbl">
        <thead>
          <tr>
            <th scope="col">Name</th>
            <th scope="col">Industry</th>
            <th scope="col">Risk</th>
            {sortableHeader('shares', 'Shares')}
            {sortableHeader('price', 'Share price')}
            {sortableHeader('cost', 'Cost estimation', 'Issued shares valued at the current share price')}
          </tr>
        </thead>
        <tbody>
          {companies.map((company) => {
            const cost = (company.issuedSharesCount ?? 0) * (company.currentPrice ?? 0)
            const isSelected = selectedId === company.id
            return (
              <tr key={company.id} className={isSelected ? 'is-selected' : undefined} aria-current={isSelected ? 'true' : undefined}>
                <th scope="row" className="cell-ellipsis">
                  <button
                    type="button"
                    className="cell-name-btn"
                    onClick={() => onSelectCompany(company.id)}
                    title={`Open ${company.name} details`}
                  >
                    {company.name}
                  </button>
                </th>
                <td className="cell-ellipsis">
                  <span className="tag">{company.industryName ?? '—'}</span>
                </td>
                <td>{company.currentRating ? <RatingBadge rating={company.currentRating} /> : <span className="muted-sub">No audit</span>}</td>
                <td className="num ta-r">{formatInt(company.issuedSharesCount)}</td>
                <td className="num ta-r">{formatMoney(company.currentPrice)}</td>
                <td className="num ta-r">{formatMoney(cost)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
