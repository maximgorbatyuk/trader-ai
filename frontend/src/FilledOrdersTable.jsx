import { formatInt, formatMoney } from './format'
import { settlementLabel } from './marketAccounting'
import { Pager } from './TableControls'

function participantName(participantId, participantNameById) {
  if (participantId == null) return 'Issuer'
  return participantNameById.get(participantId) ?? `#${participantId}`
}

export function FilledOrdersTable({
  transactions,
  total,
  page,
  pageSize,
  participantNameById,
  companyNameById,
  onPage,
  onSelectCompany,
}) {
  if (transactions.length === 0) {
    return <p className="note note-sm">No orders have been filled yet.</p>
  }

  const pageCount = Math.max(1, Math.ceil(total / pageSize))

  return (
    <>
      <div className="tbl-wrap">
        <table className="tbl" aria-label="Filled orders and settlements">
          <thead>
            <tr>
              <th scope="col" className="ta-r">Trade day</th>
              <th scope="col">Company</th>
              <th scope="col">Seller → Buyer</th>
              <th scope="col" className="ta-r">Quantity</th>
              <th scope="col" className="ta-r">Price</th>
              <th scope="col" className="ta-r">Total</th>
              <th scope="col">Settlement</th>
            </tr>
          </thead>
          <tbody>
            {transactions.map((transaction) => {
              const companyName = companyNameById.get(transaction.companyId) ?? `#${transaction.companyId}`
              const sellerName = participantName(transaction.sellerId, participantNameById)
              const buyerName = participantName(transaction.buyerId, participantNameById)
              const settlement = settlementLabel({
                status: transaction.settlementStatus,
                tradeDayNumber: transaction.tradeDayNumber,
                dueDayNumber: transaction.dueDayNumber,
              })

              return (
                <tr key={transaction.id}>
                  <td className="num ta-r">
                    {transaction.tradeDayNumber == null ? '—' : `Day ${formatInt(transaction.tradeDayNumber)}`}
                  </td>
                  <th scope="row" className={onSelectCompany ? undefined : 'cell-ellipsis'}>
                    {onSelectCompany ? (
                      <button
                        type="button"
                        className="cell-name-btn cell-ellipsis"
                        onClick={() => onSelectCompany(transaction.companyId)}
                        title={`Open ${companyName} details`}
                      >
                        {companyName}
                      </button>
                    ) : (
                      companyName
                    )}
                  </th>
                  <td className="cell-flow">
                    <span className="cell-ellipsis">{sellerName}</span>
                    <span className="flow-arrow" aria-label="to">→</span>
                    <span className="cell-ellipsis">{buyerName}</span>
                  </td>
                  <td className="num ta-r">{formatInt(transaction.quantity)}</td>
                  <td className="num ta-r">{formatMoney(transaction.price)}</td>
                  <td className="num ta-r">{formatMoney(transaction.totalCost)}</td>
                  <td>{settlement}</td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      <Pager page={page} pageCount={pageCount} onPage={onPage} />
    </>
  )
}
