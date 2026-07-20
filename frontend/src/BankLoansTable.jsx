import { Link } from 'react-router-dom'
import { formatInt, formatMoney } from './format'

// Presentational one-page loans roster. Filtering, sorting, and paging are owned by the Bank loans page (the
// server does the work); this renders the current page and reports sortable-header clicks up via onToggleSort.
export function BankLoansTable({ loans, sortKey, sortDir, onToggleSort }) {
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

  if (loans.length === 0) {
    return <p className="note">No explicit term loans match the current filter.</p>
  }

  return (
    <div className="tbl-wrap">
      <table className="tbl">
        <thead>
          <tr>
            <th scope="col">Borrower</th>
            <th scope="col">Bank</th>
            {sortableHeader('principal', 'Remaining', 'Outstanding principal still to repay')}
            {sortableHeader('pastDue', 'Principal due', 'Overdue principal not yet repaid')}
            <th scope="col" className="ta-r">
              Interest due
            </th>
            <th scope="col" className="ta-r">
              Fees
            </th>
            <th scope="col" className="ta-r">
              Total liability
            </th>
            {sortableHeader('term', 'Term', 'Loan term in trading days')}
            <th scope="col" className="ta-r">
              Remaining
            </th>
            <th scope="col">Status</th>
          </tr>
        </thead>
        <tbody>
          {loans.map((loan) => (
            <tr key={loan.id}>
              <th scope="row" className="cell-ellipsis">
                <Link className="cell-link" to={`/traders/${loan.participantId}`}>
                  {loan.participantName}
                </Link>
              </th>
              <td className="cell-ellipsis">
                <span className="tag">{loan.bankName}</span>
              </td>
              <td className="num ta-r">{formatMoney(loan.remainingPrincipal)}</td>
              <td className={`num ta-r${loan.pastDuePrincipal > 0 ? ' tone-attention' : ' muted-sub'}`}>
                {formatMoney(loan.pastDuePrincipal)}
              </td>
              <td className={`num ta-r${loan.pastDueInterest > 0 ? ' tone-attention' : ' muted-sub'}`}>
                {formatMoney(loan.pastDueInterest)}
              </td>
              <td className={`num ta-r${loan.accruedFees > 0 ? ' tone-attention' : ' muted-sub'}`}>
                {formatMoney(loan.accruedFees)}
              </td>
              <td className="num ta-r">{formatMoney(loan.totalLiability)}</td>
              <td className="num ta-r">{formatInt(loan.termTradingDays)}</td>
              <td className="num ta-r">{loan.isClosed ? '—' : `${formatInt(loan.remainingTermTradingDays)} d`}</td>
              <td>
                {loan.isClosed ? (
                  <span className="tag" title={loan.closeReason ?? undefined}>
                    Closed
                  </span>
                ) : (
                  <span className="tag tag-flag">Open</span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
