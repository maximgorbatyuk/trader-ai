import { formatInt, formatMoney, TRADER_TYPE_LABEL } from './format'
import { TemperamentTag } from './TemperamentTag'

// Presentational roster of one page of traders. Sorting, searching, type filtering, and paging are owned by
// the Traders page (the server does the work), so this component just renders the current page and reports
// header clicks up via onToggleSort. The name is a button so the page can open the detail block below it.
export function TradersTable({ participants, sortKey, sortDir, onToggleSort, onSelectTrader, selectedId }) {
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

  if (participants.length === 0) {
    return <p className="note">No traders match the current search and filter.</p>
  }

  return (
    <div className="tbl-wrap">
      <table className="tbl">
        <thead>
          <tr>
            <th scope="col">Name</th>
            <th scope="col">Type</th>
            {sortableHeader('shares', 'Shares')}
            {sortableHeader('balance', 'Current balance')}
            {sortableHeader('holdings', 'Holdings (est.)', 'Estimated market value of shares held')}
            {sortableHeader('total', 'Total', 'Current balance plus holdings estimation, less explicit term-loan and margin liabilities')}
          </tr>
        </thead>
        <tbody>
          {participants.map((participant) => {
            const estimation = participant.holdingsValue ?? 0
            const total = (participant.currentBalance ?? 0) + estimation - (participant.loanLiability ?? 0)
            const isSelected = selectedId === participant.id
            return (
              <tr key={participant.id} className={isSelected ? 'is-selected' : undefined} aria-current={isSelected ? 'true' : undefined}>
                <th scope="row">
                  <span className="cell-trader">
                    <button
                      type="button"
                      className="cell-name-btn cell-ellipsis"
                      onClick={() => onSelectTrader?.(participant)}
                      title={`Open ${participant.name} details`}
                    >
                      {participant.name}
                    </button>
                    <TemperamentTag temperament={participant.temperament} type={participant.type} />
                    {participant.type === 'CollectiveFund' ? (
                      <span className="tag tag-collective">Fund</span>
                    ) : null}
                    {participant.type === 'Player' ? <span className="tag">Player</span> : null}
                    {participant.isBankrupt ? (
                      <span className="tag tag-bankrupt">Bankrupt</span>
                    ) : null}
                  </span>
                </th>
                <td>
                  <span className="tag">
                    {participant.type === 'AIAgent' && participant.aiProviderLabel
                      ? `AI · ${participant.aiProviderLabel}`
                      : (TRADER_TYPE_LABEL[participant.type] ?? participant.type)}
                  </span>
                </td>
                <td className="num ta-r">{formatInt(participant.sharesOwned)}</td>
                <td className="num ta-r">{formatMoney(participant.currentBalance)}</td>
                <td className="num ta-r">{formatMoney(estimation)}</td>
                <td className="num ta-r">{formatMoney(total)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
