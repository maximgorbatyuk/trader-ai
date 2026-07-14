import { useState } from 'react'
import { formatCompactMoney, formatInt, formatMoney, toneOf } from './format'
import { Panel } from './Panel'
import { Treemap } from './Treemap'
import { luldPresentation } from './marketAccounting'
import { formatPct, TONE_WORD } from './treemapLayout'
import { LatestNews } from './LatestNews'

// The 25th/75th percentile of a value set, used to split companies into cheapest / average / richest bands.
function quartileBounds(values) {
  if (values.length === 0) return { p25: 0, p75: 0 }
  const sorted = [...values].sort((a, b) => a - b)
  const at = (quantile) => sorted[Math.min(sorted.length - 1, Math.floor(quantile * (sorted.length - 1)))]
  return { p25: at(0.25), p75: at(0.75) }
}

const CAP_OPTIONS = [
  { value: 'all', label: 'All caps' },
  { value: 'top', label: 'Top 25% richest' },
  { value: 'mid', label: '25–75% average' },
  { value: 'bottom', label: 'Bottom 25% poorest' },
]
const PLAYER_SHARE_OPTIONS = [
  { value: 'all', label: 'All companies' },
  { value: 'owned', label: 'Player holds' },
  { value: 'not', label: 'Player does not hold' },
]
const RISK_OPTIONS = [
  { value: 'all', label: 'Any rating' },
  { value: 'none', label: 'No audit' },
  { value: 'RaisedExpectations', label: 'Raised expectations' },
  { value: 'Extra', label: 'Extra risk' },
  { value: 'High', label: 'High risk' },
  { value: 'Low', label: 'Low risk' },
]

// The dashboard/trade-market treemap: companies sized by capitalisation, coloured by its cycle-over-cycle
// change, behind a filter bar. Prop-driven and self-contained so both pages can feed it the same data; the two
// latest news posts sit under the map. onSelectCompany decides whether a tile opens a modal or a detail route.
export function MarketMapPanel({ companies, participants, playerHoldingCompanyIds, lastDividendTotal, currentCycleNumber, news, onSelectCompany }) {
  // Tile colour tracks the change in a company's total capitalisation, not its per-share price, so a stock
  // split (shares up, price down, capitalisation unchanged) reads as flat rather than a market-wide crash.
  // Anchored to the cycle number, not the poll: the move is measured against the previous cycle's caps and the
  // colour holds through every poll of the current cycle, only re-computing when a new cycle advances.
  const [capChange, setCapChange] = useState({ cycle: null, capById: new Map(), changeById: new Map() })
  const [industrySel, setIndustrySel] = useState(() => new Set())
  const [capBucket, setCapBucket] = useState('all')
  const [playerSel, setPlayerSel] = useState('all')
  const [riskSel, setRiskSel] = useState('all')

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

  // Industry options and cap bands are derived from the whole priced market, so a band means "richest in the
  // market" and the industry list stays stable regardless of the other active filters.
  const industryOptions = [...new Set(mappedCompanies.map((company) => company.industryName).filter(Boolean))].sort()
  const { p25, p75 } = quartileBounds(mappedCompanies.map((company) => company.capitalization))

  function matchesCap(cap) {
    if (capBucket === 'top') return cap >= p75
    if (capBucket === 'bottom') return cap <= p25
    if (capBucket === 'mid') return cap > p25 && cap < p75
    return true
  }

  function matchesRisk(company) {
    if (riskSel === 'all') return true
    if (riskSel === 'none') return !company.currentRating
    return company.currentRating === riskSel
  }

  const visibleCompanies = mappedCompanies.filter((company) => {
    if (industrySel.size > 0 && !industrySel.has(company.industryName)) return false
    if (!matchesCap(company.capitalization)) return false
    if (playerSel === 'owned' && !heldIds.has(company.id)) return false
    if (playerSel === 'not' && heldIds.has(company.id)) return false
    return matchesRisk(company)
  })

  const totalShares = visibleCompanies.reduce((sum, company) => sum + company.issuedSharesCount, 0)

  // Capitalisation values every issued share at its company's current price, matching the tile areas;
  // participant cash is the cash side of the same market.
  const totalCapitalization = visibleCompanies.reduce((sum, company) => sum + company.capitalization, 0)
  const totalParticipantMoney = participants.reduce(
    (sum, participant) => sum + (participant.currentBalance ?? 0),
    0,
  )

  function toggleIndustry(name) {
    setIndustrySel((current) => {
      const next = new Set(current)
      if (next.has(name)) next.delete(name)
      else next.add(name)
      return next
    })
  }

  const mapItems = visibleCompanies.map((company) => {
    const tone = toneOf(company.capChangePct)
    const luld = luldPresentation(company.luldState)
    const haltSuffix = luld.orderEntryDisabled ? ` · ${luld.label}` : ''
    return {
      id: company.id,
      label: company.name,
      value: company.capitalization,
      changePct: company.capChangePct,
      halted: luld.orderEntryDisabled ? `${luld.indicator} ${luld.label}` : null,
      title: `${company.name} · ${formatCompactMoney(company.capitalization)} cap · ${formatInt(company.issuedSharesCount)} shares · ${formatMoney(company.currentPrice)} · ${formatPct(company.capChangePct)}${haltSuffix}`,
      ariaLabel: `${company.name}, ${formatCompactMoney(company.capitalization)} capitalisation, ${formatInt(company.issuedSharesCount)} issued shares, ${formatMoney(company.currentPrice)}, ${TONE_WORD[tone]} ${formatPct(company.capChangePct)}${haltSuffix}. Open details.`,
    }
  })

  const industryLabel = industrySel.size === 0 ? 'All industries' : `${industrySel.size} selected`

  return (
    <Panel
      title="Market map"
      count={mappedCompanies.length ? `${visibleCompanies.length} companies · ${formatInt(totalShares)} shares` : undefined}
      className="panel-map"
    >
      {mappedCompanies.length === 0 ? (
        <p className="note">Seed the market to see company prices.</p>
      ) : (
        <>
        <div className="map-filters">
          <details className="filter-multi">
            <summary>
              Industry<span className="filter-multi-value">{industryLabel}</span>
            </summary>
            <div className="filter-multi-menu">
              {industrySel.size > 0 ? (
                <button type="button" className="btn select-sm filter-multi-clear" onClick={() => setIndustrySel(new Set())}>
                  Clear ({industrySel.size})
                </button>
              ) : null}
              {industryOptions.map((name) => (
                <label key={name} className="industry-check">
                  <input type="checkbox" checked={industrySel.has(name)} onChange={() => toggleIndustry(name)} />
                  <span>{name}</span>
                </label>
              ))}
            </div>
          </details>
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
            <span className="filter-label">Risk auditor</span>
            <select className="select select-sm" value={riskSel} onChange={(event) => setRiskSel(event.target.value)}>
              {RISK_OPTIONS.map((option) => (
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
      <LatestNews news={news} currentCycleNumber={currentCycleNumber} onSelectCompany={onSelectCompany} count={2} />
    </Panel>
  )
}
