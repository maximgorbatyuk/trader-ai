import { Link } from 'react-router-dom'
import { favoriteCompanies } from './favoriteCompanies'
import { formatCompactMoney, formatMoney, toneOf } from './format'
import { RatingBadge } from './RatingBadge'
import { formatPct, TONE_GLYPH } from './treemapLayout'

export function FavoriteCompaniesTable({ companies, onSelectCompany }) {
  const rows = favoriteCompanies(companies)

  if (rows.length === 0) {
    return <p className="note">No favorite companies yet. Mark one from a company header to keep it here.</p>
  }

  return (
    <div className="tbl-wrap">
      <table className="tbl">
        <thead>
          <tr>
            <th scope="col">Company</th>
            <th scope="col">Industry</th>
            <th scope="col" className="ta-r">Price</th>
            <th scope="col" className="ta-r">Change</th>
            <th scope="col" className="ta-r">Market cap</th>
            <th scope="col">Rating</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((company) => {
            const changeTone = toneOf(company.priceChangePct)
            const capitalization = company.issuedSharesCount * (company.currentPrice ?? 0)
            return (
              <tr key={company.id}>
                <th scope="row">
                  <span className="favorite-company-name">
                    <span className="favorite-company-marker" role="img" aria-label="Favorite company">★</span>
                    {onSelectCompany ? (
                      <button
                        type="button"
                        className="cell-name-btn cell-ellipsis"
                        onClick={() => onSelectCompany(company.id)}
                        title={`Open ${company.name} details`}
                      >
                        {company.name}
                      </button>
                    ) : (
                      <Link className="cell-link cell-ellipsis" to={`/companies/${company.id}`}>
                        {company.name}
                      </Link>
                    )}
                  </span>
                </th>
                <td>{company.industryName ?? '—'}</td>
                <td className="num ta-r">{formatMoney(company.currentPrice)}</td>
                <td className={`num ta-r tone-${changeTone}`}>
                  <span aria-hidden="true">{TONE_GLYPH[changeTone]} </span>{formatPct(company.priceChangePct)}
                </td>
                <td className="num ta-r" title={formatMoney(capitalization)}>{formatCompactMoney(capitalization)}</td>
                <td><RatingBadge rating={company.currentRating} /></td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
