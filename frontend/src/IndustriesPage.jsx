import { useCallback, useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatCompactMoney, formatInt, formatMoney, toneOf } from './format'
import { MultiLineChart } from './MultiLineChart'
import { Panel } from './Panel'
import { SortHeader, Pager } from './TableControls'
import { Treemap } from './Treemap'
import { TONE_WORD } from './treemapLayout'
import { useClientTable } from './useClientTable'

const POLL_INTERVAL_MS = 2500
const CHANGE_GLYPH = { up: '▲', down: '▼', flat: '–' }

function formatSentiment(value) {
  if (typeof value !== 'number') return '—'
  return `${value > 0 ? '+' : ''}${value}`
}

function formatSentimentChange(value) {
  if (typeof value !== 'number') return '—'
  return `${value > 0 ? '+' : ''}${value} pts`
}

function formatDecimal(value) {
  return typeof value === 'number' ? value.toFixed(2) : '—'
}

// The industry roster reads sentiment from the server. Company values only supply the treemap's current area,
// keeping price-capitalisation aggregation separate from the sentiment model and its history.
function IndustriesPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [industries, setIndustries] = useState([])
  const [companies, setCompanies] = useState([])
  const [history, setHistory] = useState([])
  const navigate = useNavigate()

  const loadAll = useCallback(async () => {
    try {
      const [industryData, companyData, historyData] = await Promise.all([
        api.getIndustries(),
        api.getCompanies(),
        api.getIndustriesSentimentHistory(),
      ])
      setIndustries(industryData ?? [])
      setCompanies(companyData ?? [])
      setHistory(historyData ?? [])
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll])

  const rows = useMemo(() => {
    const worthByIndustry = new Map()
    const countByIndustry = new Map()
    for (const company of companies) {
      const worth = (company.issuedSharesCount ?? 0) * (company.currentPrice ?? 0)
      worthByIndustry.set(company.industryId, (worthByIndustry.get(company.industryId) ?? 0) + worth)
      countByIndustry.set(company.industryId, (countByIndustry.get(company.industryId) ?? 0) + 1)
    }
    return industries.map((industry) => ({
      ...industry,
      totalWorth: worthByIndustry.get(industry.id) ?? 0,
      companyCount: countByIndustry.get(industry.id) ?? 0,
    }))
  }, [companies, industries])

  const { pageRows, sortKey, sortDir, toggleSort, page, pageCount, setPage } = useClientTable(rows, {
    pageSize: 10,
    initialSortKey: 'totalWorth',
  })
  const totalWorth = rows.reduce((sum, industry) => sum + industry.totalWorth, 0)
  const sentimentSeries = history.map((series) => ({ name: series.industryName, points: series.points }))
  const treemapItems = rows
    .filter((industry) => industry.totalWorth > 0)
    .map((industry) => {
      const tone = toneOf(industry.lastCycleSentimentChange)
      return {
        id: industry.id,
        label: industry.name,
        value: industry.totalWorth,
        changePct: industry.lastCycleSentimentChange,
        title: `${industry.name} · ${formatCompactMoney(industry.totalWorth)} · ${formatInt(industry.companyCount)} companies · sentiment ${formatSentimentChange(industry.lastCycleSentimentChange)}`,
        ariaLabel: `${industry.name}, ${formatCompactMoney(industry.totalWorth)} total worth, ${formatInt(industry.companyCount)} companies, sentiment ${TONE_WORD[tone]} ${formatSentimentChange(industry.lastCycleSentimentChange)}. Open industry details.`,
      }
    })

  return (
    <main className="main">
      {!ready ? (
        <section className="placeholder" aria-busy="true">
          <span className="spinner" aria-hidden="true" />
          <p>Loading industries…</p>
        </section>
      ) : (
        <>
          {loadError ? (
            <div className="banner" role="alert">
              <strong>Showing last known state.</strong>
              <span>{loadError}</span>
            </div>
          ) : null}

          <Panel
            title="Industry map"
            count={treemapItems.length ? `${treemapItems.length} industries · ${formatCompactMoney(totalWorth)}` : undefined}
            className="panel-map"
          >
            {treemapItems.length === 0 ? (
              <p className="note">Seed the market to see industries.</p>
            ) : (
              <div className="map-layout">
                <Treemap
                  items={treemapItems}
                  onSelect={(industryId) => navigate(`/industries/${industryId}`)}
                  formatValue={formatCompactMoney}
                  formatChange={formatSentimentChange}
                  ariaLabel="Industries by total worth"
                />
              </div>
            )}
          </Panel>

          <Panel title="All industry sentiment" count={`${formatInt(sentimentSeries.length)} series`} className="panel-holdings">
            {sentimentSeries.length === 0 ? (
              <p className="note">No sentiment history has been recorded yet.</p>
            ) : (
              <MultiLineChart series={sentimentSeries} formatValue={formatSentiment} label="Sentiment history for all industries" />
            )}
          </Panel>

          <Panel title="Industries" count={`${formatInt(rows.length)}`} className="panel-holdings">
            {rows.length === 0 ? (
              <p className="note">No industries yet.</p>
            ) : (
              <>
                <div className="tbl-wrap">
                  <table className="tbl">
                    <thead>
                      <tr>
                        <th scope="col">Name</th>
                        <th scope="col" className="ta-r">
                          Companies
                        </th>
                        <SortHeader label="Total worth" columnKey="totalWorth" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                        <SortHeader label="Sentiment" columnKey="sentimentValue" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                        <SortHeader label="Volatility" columnKey="sentimentVolatility" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                        <SortHeader label="Beta" columnKey="sectorBeta" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                        <SortHeader
                          label="Last cycle"
                          columnKey="lastCycleSentimentChange"
                          sortKey={sortKey}
                          sortDir={sortDir}
                          onToggle={toggleSort}
                          title="Change in sentiment since the preceding cycle"
                        />
                      </tr>
                    </thead>
                    <tbody>
                      {pageRows.map((industry) => {
                        const tone = toneOf(industry.lastCycleSentimentChange)
                        return (
                          <tr key={industry.id}>
                            <th scope="row" className="cell-ellipsis">
                              <button
                                type="button"
                                className="cell-name-btn"
                                onClick={() => navigate(`/industries/${industry.id}`)}
                                title={`Open ${industry.name} industry details`}
                              >
                                {industry.name}
                              </button>
                            </th>
                            <td className="num ta-r">{formatInt(industry.companyCount)}</td>
                            <td className="num ta-r">{formatMoney(industry.totalWorth)}</td>
                            <td className="num ta-r">{formatSentiment(industry.sentimentValue)}</td>
                            <td className="num ta-r">{formatDecimal(industry.sentimentVolatility)}</td>
                            <td className="num ta-r">{formatDecimal(industry.sectorBeta)}</td>
                            <td className={`num ta-r tone-${tone}`}>
                              <span aria-hidden="true">{CHANGE_GLYPH[tone]} </span>
                              {formatSentimentChange(industry.lastCycleSentimentChange)}
                            </td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                </div>
                <Pager page={page} pageCount={pageCount} onPage={setPage} />
              </>
            )}
          </Panel>
        </>
      )}
    </main>
  )
}

export default IndustriesPage
