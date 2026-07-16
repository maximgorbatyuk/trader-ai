import { Link } from 'react-router-dom'
import { formatCompactMoney, formatInt, formatMoney } from './format'

// Shared table for big-investment deals. The identity columns shown depend on context: the company page hides
// the company column, a participant page hides the investor column, and the market feed shows both. The first
// visible identity column is the row header for accessibility.
export function InvestmentsTable({ investments, showCompany = true, showInvestor = true, emptyLabel = 'No investments yet.' }) {
  if (!investments || investments.length === 0) {
    return <p className="note">{emptyLabel}</p>
  }

  return (
    <div className="tbl-wrap">
      <table className="tbl">
        <thead>
          <tr>
            {showCompany ? <th scope="col">Company</th> : null}
            {showInvestor ? <th scope="col">Investor</th> : null}
            <th scope="col" className="ta-r">Deal value</th>
            <th scope="col" className="ta-r">Shares</th>
            <th scope="col" className="ta-r">Stake</th>
            <th scope="col" className="ta-r">Cap before</th>
            <th scope="col" className="ta-r">Cap after</th>
            <th scope="col" className="ta-r">When</th>
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
