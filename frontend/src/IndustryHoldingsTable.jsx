import { Link } from 'react-router-dom'
import { formatInt, formatMoney, formatSigned, toneOf } from './format'
import { groupHoldingsByIndustry } from './industryHoldings'
import { SortHeader } from './TableControls'

// The industry exposure breakdown shared by the dashboard player block and the participant detail page; the
// caller wraps it in its own chrome (a tab panel or a Panel). Two modes: pass raw holdings + companies for
// client-side grouping (dashboard), or pass already-aggregated, server-paged rows plus sort props to get
// sortable headers (participant detail).
export function IndustryHoldingsTable({ holdings, companies, rows, sortKey, sortDir, onToggleSort, emptyNote = 'No shares held yet.' }) {
  const data = rows ?? groupHoldingsByIndustry(holdings, companies)
  const sortable = typeof onToggleSort === 'function'

  if (data.length === 0) {
    return <p className="note note-sm">{emptyNote}</p>
  }

  function header(label, columnKey, align) {
    if (sortable) {
      return <SortHeader label={label} columnKey={columnKey} sortKey={sortKey} sortDir={sortDir} onToggle={onToggleSort} align={align} />
    }
    return (
      <th scope="col" className={align === 'right' ? 'ta-r' : undefined}>
        {label}
      </th>
    )
  }

  return (
    <div className="tbl-wrap">
      <table className="tbl">
        <thead>
          <tr>
            {header('Industry', 'industry', 'left')}
            {header('Companies', 'companies', 'right')}
            {header('Shares', 'shares', 'right')}
            {header('Value', 'value', 'right')}
            {header('% of portfolio', 'pct', 'right')}
            {header('P/L', 'pnl', 'right')}
          </tr>
        </thead>
        <tbody>
          {data.map((row) => (
            <tr key={row.industryId ?? row.industryName}>
              <th scope="row" className="cell-ellipsis">
                {row.industryId != null ? (
                  <Link className="cell-link" to={`/industries/${row.industryId}`}>
                    {row.industryName}
                  </Link>
                ) : (
                  row.industryName
                )}
              </th>
              <td className="num ta-r">{formatInt(row.companyCount)}</td>
              <td className="num ta-r">{formatInt(row.shares)}</td>
              <td className="num ta-r">{formatMoney(row.value)}</td>
              <td className="num ta-r">{(row.pct * 100).toFixed(1)}%</td>
              <td className={`num ta-r tone-${toneOf(row.pnl)}`}>{formatSigned(row.pnl)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
