import { formatInt, formatMoney } from './format'
import { settlementLabel } from './marketAccounting'

export function SettlementsTable({ settlements, emptyNote = 'No pending settlements.', onSelectCompany }) {
  if (settlements.length === 0) {
    return <p className="note note-sm">{emptyNote}</p>
  }

  return (
    <div className="tbl-wrap">
      <table className="tbl">
        <thead>
          <tr>
            <th scope="col">Side</th>
            <th scope="col">Company</th>
            <th scope="col" className="ta-r">Quantity</th>
            <th scope="col" className="ta-r">Cash</th>
            <th scope="col" className="ta-r">Trade day</th>
            <th scope="col">Settlement</th>
          </tr>
        </thead>
        <tbody>
          {settlements.map((settlement) => (
            <tr key={settlement.id}>
              <td className={`tone-${settlement.side === 'Buy' ? 'up' : 'down'}`}>{settlement.side}</td>
              <th scope="row" className={onSelectCompany ? undefined : 'cell-ellipsis'}>
                {onSelectCompany ? (
                  <button
                    type="button"
                    className="cell-name-btn cell-ellipsis"
                    onClick={() => onSelectCompany(settlement.companyId)}
                    title={`Open ${settlement.companyName} details`}
                  >
                    {settlement.companyName}
                  </button>
                ) : (
                  settlement.companyName
                )}
              </th>
              <td className="num ta-r">{formatInt(settlement.quantity)}</td>
              <td className="num ta-r">{formatMoney(settlement.cashAmount)}</td>
              <td className="num ta-r">Day {formatInt(settlement.tradeDayNumber)}</td>
              <td>{settlementLabel(settlement)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
