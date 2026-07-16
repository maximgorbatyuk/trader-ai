import { Link } from 'react-router-dom'
import { formatCompactMoney, formatInt, formatMoney } from './format'
import { SortHeader } from './TableControls'

// Shared table for big-investment deals. The identity columns shown depend on context: the company page hides
// the company column, a participant page hides the investor column, and the market feed shows both. The first
// visible identity column is the row header for accessibility. Passing sort props (participant detail) turns the
// numeric columns and Company into sortable, server-driven headers; without them the headers render statically.
export function InvestmentsTable({ investments, showCompany = true, showInvestor = true, sortKey, sortDir, onToggleSort, emptyLabel = 'No investments yet.' }) {
  if (!investments || investments.length === 0) {
    return <p className="note">{emptyLabel}</p>
  }

  const sortable = typeof onToggleSort === 'function'
  const header = (label, columnKey, align = 'right') => {
    if (sortable && columnKey) {
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
            {showCompany ? header('Company', 'company', 'left') : null}
            {showInvestor ? header('Investor', null, 'left') : null}
            {header('Deal value', 'dealValue')}
            {header('Shares', 'shares')}
            {header('Stake', 'stake')}
            {header('Cap before', 'capBefore')}
            {header('Cap after', 'capAfter')}
            {header('When', 'when')}
          </tr>
        </thead>
        <tbody>
          {investments.map((investment) => {
            const investorLink = (
              <Link
                className="cell-link"
                to={`/traders/${investment.investorParticipantId}`}
                title={`Open ${investment.investorName ?? 'trader'} trader page`}
              >
                {investment.investorName ?? `#${investment.investorParticipantId}`}
              </Link>
            )

            return (
              <tr key={investment.id}>
                {showCompany ? (
                  <th scope="row" className="cell-ellipsis">
                    <Link className="cell-link" to={`/companies/${investment.companyId}`} title={`Open ${investment.companyName}`}>
                      {investment.companyName}
                    </Link>
                  </th>
                ) : null}
                {showInvestor
                  ? showCompany
                    ? <td className="cell-ellipsis">{investorLink}</td>
                    : <th scope="row" className="cell-ellipsis">{investorLink}</th>
                  : null}
                <td className="num ta-r">{formatMoney(investment.dealValue)}</td>
                <td className="num ta-r">
                  {formatInt(investment.sharesIssued)}
                  <span className="muted-sub"> from {formatInt(investment.sharesBeforeDeal)}</span>
                </td>
                <td className="num ta-r">{(investment.investorSharePercent ?? 0).toFixed(2)}%</td>
                <td className="num ta-r">{formatCompactMoney(investment.capitalizationBeforeDeal)}</td>
                <td className="num ta-r">{formatCompactMoney(investment.finalCapitalization)}</td>
                <td className="num ta-r">
                  {typeof investment.tradingDayNumber === 'number' ? `Day ${formatInt(investment.tradingDayNumber)}` : '—'}
                  <span className="muted-sub"> · #{formatInt(investment.createdInCycleNumber)}</span>
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
