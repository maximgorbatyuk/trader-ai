import { Link } from 'react-router-dom'
import { formatInt, formatMoney, formatSigned, toneOf } from './format'
import { groupHoldingsByIndustry } from './industryHoldings'

// The industry exposure breakdown shared by the dashboard player block and the participant detail page; the
// caller wraps it in its own chrome (a tab panel or a Panel).
export function IndustryHoldingsTable({ holdings, companies, emptyNote = 'No shares held yet.' }) {
  const rows = groupHoldingsByIndustry(holdings, companies)

  if (rows.length === 0) {
    return <p className="note note-sm">{emptyNote}</p>
  }

  return (
    <div className="tbl-wrap">
      <table className="tbl">
        <thead>
          <tr>
            <th scope="col">Industry</th>
            <th scope="col" className="ta-r">
              Companies
            </th>
            <th scope="col" className="ta-r">
              Shares
            </th>
            <th scope="col" className="ta-r">
              Value
            </th>
            <th scope="col" className="ta-r">
              % of portfolio
            </th>
            <th scope="col" className="ta-r">
              P/L
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
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
