import { Link } from 'react-router-dom'
import { favoriteTraders } from './favoriteTraders'
import { formatInt, formatMoney } from './format'

const TYPE_LABEL = { Individual: 'Individual', Company: 'Company', AIAgent: 'AI agent', CollectiveFund: 'Collective fund', Player: 'Player' }

export function FavoriteTradersTable({ participants }) {
  const rows = favoriteTraders(participants)

  if (rows.length === 0) {
    return <p className="note">No favorite traders yet. Mark one from a trader's page to keep it here.</p>
  }

  return (
    <div className="tbl-wrap">
      <table className="tbl">
        <thead>
          <tr>
            <th scope="col">Trader</th>
            <th scope="col">Type</th>
            <th scope="col" className="ta-r">Net worth</th>
            <th scope="col" className="ta-r">Cash balance</th>
            <th scope="col" className="ta-r">Shares owned</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((participant) => (
            <tr key={participant.id}>
              <th scope="row">
                <span className="favorite-company-name">
                  <span className="favorite-company-marker" role="img" aria-label="Favorite trader">★</span>
                  <Link className="cell-link cell-ellipsis" to={`/traders/${participant.id}`}>
                    {participant.name}
                  </Link>
                </span>
              </th>
              <td>{TYPE_LABEL[participant.type] ?? participant.type}</td>
              <td className="num ta-r">{formatMoney(participant.totalWorth)}</td>
              <td className="num ta-r">{formatMoney(participant.currentBalance)}</td>
              <td className="num ta-r">{formatInt(participant.sharesOwned)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
