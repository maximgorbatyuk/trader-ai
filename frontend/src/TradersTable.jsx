import { useState } from 'react'
import { formatInt, formatMoney, TRADER_TYPE_LABEL } from './format'
import { TemperamentTag } from './TemperamentTag'

// Net worth proxy used for the Total column and its default sort: cash on hand plus the estimated
// market value of shares held.
const TRADER_SORTS = {
  balance: (participant) => participant.currentBalance ?? 0,
  estimation: (participant) => participant.holdingsValue ?? 0,
  total: (participant) => (participant.currentBalance ?? 0) + (participant.holdingsValue ?? 0),
}

// Sortable, type-filterable roster of traders. The name is a button so the same table drives both the
// dashboard summary modal and the Traders page detail block via onSelectTrader; selectedId highlights the
// row whose detail is open below the table on the Traders page.
export function TradersTable({ participants, onSelectTrader, selectedId }) {
  const [sortKey, setSortKey] = useState('total')
  const [sortDir, setSortDir] = useState('desc')
  const [typeFilter, setTypeFilter] = useState('all')

  function toggleSort(key) {
    if (key === sortKey) {
      setSortDir((dir) => (dir === 'desc' ? 'asc' : 'desc'))
    } else {
      setSortKey(key)
      setSortDir('desc')
    }
  }

  const selector = TRADER_SORTS[sortKey]
  const sorted = [...participants].sort((a, b) => {
    const diff = selector(a) - selector(b)
    return sortDir === 'desc' ? -diff : diff
  })
  const visible = typeFilter === 'all' ? sorted : sorted.filter((participant) => participant.type === typeFilter)

  const counts = participants.reduce(
    (acc, participant) => {
      if (participant.type in acc) acc[participant.type] += 1
      return acc
    },
    { Individual: 0, AIAgent: 0, CollectiveFund: 0, Player: 0 },
  )

  function sortableHeader(key, label, title) {
    const active = sortKey === key
    return (
      <th scope="col" className="ta-r" aria-sort={active ? (sortDir === 'desc' ? 'descending' : 'ascending') : 'none'}>
        <button
          type="button"
          className={`th-sort ${active ? 'is-active' : ''}`}
          onClick={() => toggleSort(key)}
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
    return <p className="note">No participants yet.</p>
  }

  return (
    <>
      <div className="tabpanel-toolbar">
        <p className="trader-stats">
          {counts.Individual} individuals · {counts.AIAgent} AI · {counts.CollectiveFund} funds
          {counts.Player > 0 ? ` · ${counts.Player} player` : ''} · Total: {participants.length}
        </p>
        <select
          className="select select-sm"
          aria-label="Filter traders by type"
          value={typeFilter}
          onChange={(event) => setTypeFilter(event.target.value)}
        >
          <option value="all">All</option>
          <option value="AIAgent">AI</option>
          <option value="Individual">Individual</option>
          <option value="CollectiveFund">Fund</option>
          <option value="Player">Player</option>
        </select>
      </div>
      {visible.length === 0 ? (
        <p className="note">No traders of this type.</p>
      ) : (
        <div className="tbl-scroll">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Type</th>
                <th scope="col" className="ta-r">
                  Shares
                </th>
                {sortableHeader('balance', 'Current balance')}
                {sortableHeader('estimation', 'Holdings (est.)', 'Estimated market value of shares held')}
                {sortableHeader('total', 'Total', 'Current balance plus holdings estimation')}
              </tr>
            </thead>
            <tbody>
              {visible.map((participant) => {
                const estimation = participant.holdingsValue ?? 0
                const total = (participant.currentBalance ?? 0) + estimation
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
                      <span className="tag">{TRADER_TYPE_LABEL[participant.type] ?? participant.type}</span>
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
      )}
    </>
  )
}
