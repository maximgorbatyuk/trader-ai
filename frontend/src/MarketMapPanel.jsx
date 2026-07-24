import { useState } from 'react'
import { formatCompactMoney, formatInt, formatMoney, toneOf } from './format'
import { Panel } from './Panel'
import { Treemap } from './Treemap'
import { luldPresentation } from './marketAccounting'
import { formatPct, MARKET_MAP_BOX_H, TONE_WORD } from './treemapLayout'
import { LatestNews } from './LatestNews'
import { matchesFavoriteFilter } from './favoriteCompanies'

// Percentile breakpoints of a value set, used to split companies into capitalisation bands from richest to poorest.
function capBandBounds(values) {
  if (values.length === 0) return { p25: 0, p50: 0, p75: 0, p85: 0, p90: 0 }
  const sorted = [...values].sort((a, b) => a - b)
  const at = (quantile) => sorted[Math.min(sorted.length - 1, Math.floor(quantile * (sorted.length - 1)))]
  return { p25: at(0.25), p50: at(0.5), p75: at(0.75), p85: at(0.85), p90: at(0.9) }
}

const CAP_OPTIONS = [
  { value: 'all', label: 'All caps' },
  { value: 'top10', label: 'Top 10%' },
  { value: 'p10_15', label: '10–15%' },
  { value: 'p15_25', label: '15–25%' },
  { value: 'p25_50', label: '25–50%' },
  { value: 'p50_75', label: '50–75%' },
  { value: 'bottom25', label: 'Bottom 25%' },
]
const PLAYER_SHARE_OPTIONS = [
  { value: 'all', label: 'All companies' },
  { value: 'owned', label: 'Player holds' },
  { value: 'not', label: 'Player does not hold' },
]
const FAVORITE_OPTIONS = [
  { value: 'all', label: 'All companies' },
  { value: 'favorite', label: 'Favorite companies' },
  { value: 'not-favorite', label: 'Not favorite' },
]
const AUDIT_RATING_OPTIONS = [
  { value: 'all', label: 'Any rating' },
  { value: 'none', label: 'No audit' },
  { value: 'ExtraRaisedExpectations', label: 'Extra raised expectations' },
  { value: 'RaisedExpectations', label: 'Raised expectations' },
  { value: 'Stable', label: 'Stable' },
  { value: 'LowRisk', label: 'Low risk' },
  { value: 'HighRisk', label: 'High risk' },
]

// Keeping the update feed inside the map makes event priority consistent anywhere this market view is reused.
export function MarketMapPanel({
  companies,
  participants,
  playerHoldingCompanyIds,
  lastDividendTotal,
  currentCycleNumber,
  news,
  crises,
  scienceInvestigations,
  onSelectCompany,
  embedded = false,
}) {
  // Tile colour tracks the change in a company's total capitalisation, not its per-share price, so a stock
  // split (shares up, price down, capitalisation unchanged) reads as flat rather than a market-wide crash.
  // Anchored to the cycle number, not the poll: the move is measured against the previous cycle's caps and the
  // colour holds through every poll of the current cycle, only re-computing when a new cycle advances.
  const [capChange, setCapChange] = useState({ cycle: null, capById: new Map(), changeById: new Map() })
  const [capBucket, setCapBucket] = useState('all')
  const [playerSel, setPlayerSel] = useState('all')
  const [favoriteSel, setFavoriteSel] = useState('all')
  const [ratingSel, setRatingSel] = useState('all')

  if (capChange.cycle !== currentCycleNumber) {
    const previousCaps = capChange.capById
    const capById = new Map()
    const changeById = new Map()
    for (const company of companies) {
      const cap = company.issuedSharesCount * (company.currentPrice ?? 0)
      capById.set(company.id, cap)
      const previousCap = previousCaps.get(company.id)
      changeById.set(company.id, previousCap > 0 ? (cap - previousCap) / previousCap : 0)
    }
    setCapChange({ cycle: currentCycleNumber, capById, changeById })
  }
  const capChangeById = capChange.changeById
  const heldIds = playerHoldingCompanyIds ?? new Set()

  const mappedCompanies = companies
    .map((company) => ({
      ...company,
      capitalization: company.issuedSharesCount * (company.currentPrice ?? 0),
      capChangePct: capChangeById.get(company.id) ?? 0,
    }))
    .filter((company) => company.capitalization > 0)
    .sort((a, b) => b.capitalization - a.capitalization)

  // Cap bands are derived from the whole priced market, so a band means "richest in the market" regardless of
  // the other active filters.
  const { p25, p50, p75, p85, p90 } = capBandBounds(mappedCompanies.map((company) => company.capitalization))

  // Bands are contiguous half-open ranges from the richest down, so together they cover the whole priced market.
  function matchesCap(cap) {
    if (capBucket === 'top10') return cap >= p90
    if (capBucket === 'p10_15') return cap >= p85 && cap < p90
    if (capBucket === 'p15_25') return cap >= p75 && cap < p85
    if (capBucket === 'p25_50') return cap >= p50 && cap < p75
    if (capBucket === 'p50_75') return cap >= p25 && cap < p50
    if (capBucket === 'bottom25') return cap < p25
    return true
  }

  function matchesRating(company) {
    if (ratingSel === 'all') return true
    if (ratingSel === 'none') return !company.currentRating
    return company.currentRating === ratingSel
  }

  const visibleCompanies = mappedCompanies.filter((company) => {
    if (!matchesCap(company.capitalization)) return false
    if (playerSel === 'owned' && !heldIds.has(company.id)) return false
    if (playerSel === 'not' && heldIds.has(company.id)) return false
    if (!matchesFavoriteFilter(company, favoriteSel)) return false
    return matchesRating(company)
  })

  const totalShares = visibleCompanies.reduce((sum, company) => sum + company.issuedSharesCount, 0)

  // Capitalisation values every issued share at its company's current price, matching the tile areas;
  // participant cash is the cash side of the same market.
  const totalCapitalization = visibleCompanies.reduce((sum, company) => sum + company.capitalization, 0)
  const totalParticipantMoney = participants.reduce(
    (sum, participant) => sum + (participant.currentBalance ?? 0),
    0,
  )

  const mapItems = visibleCompanies.map((company) => {
    const tone = toneOf(company.capChangePct)
    const luld = luldPresentation(company.luldState)
    const haltSuffix = luld.orderEntryDisabled ? ` · ${luld.label}` : ''
    const favoriteSuffix = company.isFavorite ? ' · Favorite' : ''
    return {
      id: company.id,
      label: company.name,
      value: company.capitalization,
      changePct: company.capChangePct,
      halted: luld.orderEntryDisabled ? `${luld.indicator} ${luld.label}` : null,
      favorite: Boolean(company.isFavorite),
      title: `${company.name} · ${formatCompactMoney(company.capitalization)} cap · ${formatInt(company.issuedSharesCount)} shares · ${formatMoney(company.currentPrice)} · ${formatPct(company.capChangePct)}${haltSuffix}${favoriteSuffix}`,
      ariaLabel: `${company.name}, ${formatCompactMoney(company.capitalization)} capitalisation, ${formatInt(company.issuedSharesCount)} issued shares, ${formatMoney(company.currentPrice)}, ${TONE_WORD[tone]} ${formatPct(company.capChangePct)}${haltSuffix}${favoriteSuffix}. Open details.`,
    }
  })

  const countText = mappedCompanies.length
    ? `${visibleCompanies.length} companies · ${formatInt(totalShares)} shares`
    : undefined

  const body = (
    <>
      {mappedCompanies.length === 0 ? (
        <div className="market-map-empty">
          <span>Trader AI</span>
        </div>
      ) : (
        <>
        <div className="map-filters">
          <label className="filter-field">
            <span className="filter-label">Capitalization</span>
            <select className="select select-sm" value={capBucket} onChange={(event) => setCapBucket(event.target.value)}>
              {CAP_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
          <label className="filter-field">
            <span className="filter-label">Player shares</span>
            <select className="select select-sm" value={playerSel} onChange={(event) => setPlayerSel(event.target.value)}>
              {PLAYER_SHARE_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
          <label className="filter-field">
            <span className="filter-label">Player favorites</span>
            <select className="select select-sm" value={favoriteSel} onChange={(event) => setFavoriteSel(event.target.value)}>
              {FAVORITE_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
          <label className="filter-field">
            <span className="filter-label">Audit status</span>
            <select className="select select-sm" value={ratingSel} onChange={(event) => setRatingSel(event.target.value)}>
              {AUDIT_RATING_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
        </div>
        {visibleCompanies.length === 0 ? (
          <p className="note">No companies match these filters.</p>
        ) : (
        <div className="map-layout">
        <Treemap
          items={mapItems}
          onSelect={onSelectCompany}
          formatValue={formatCompactMoney}
          ariaLabel="Companies by capitalisation"
          boxHeight={MARKET_MAP_BOX_H}
        />
        <aside className="map-stats">
          <div className="map-stat">
            <span className="map-stat-label">Total cap</span>
            <span className="map-stat-value num" title={formatMoney(totalCapitalization)}>
              {formatCompactMoney(totalCapitalization)}
            </span>
          </div>
          <div className="map-stat">
            <span className="map-stat-label">Trader cash</span>
            <span className="map-stat-value num" title={formatMoney(totalParticipantMoney)}>
              {formatCompactMoney(totalParticipantMoney)}
            </span>
          </div>
          <div className="map-stat">
            <span className="map-stat-label">Last dividends</span>
            <span className="map-stat-value num" title={formatMoney(lastDividendTotal)}>
              {formatCompactMoney(lastDividendTotal)}
            </span>
          </div>
        </aside>
        </div>
        )}
        </>
      )}
      <LatestNews
        news={news}
        crises={crises}
        scienceInvestigations={scienceInvestigations}
        currentCycleNumber={currentCycleNumber}
        onSelectCompany={onSelectCompany}
        count={2}
      />
    </>
  )

  if (embedded) {
    return (
      <div className="map-embedded">
        {countText ? <p className="map-embedded-count num">{countText}</p> : null}
        {body}
      </div>
    )
  }

  return (
    <Panel title="Market map" count={countText} className="panel-map">
      {body}
    </Panel>
  )
}
