import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatCompactMoney, formatInt, formatMoney, toneOf } from './format'
import { MultiLineChart } from './MultiLineChart'
import { SortHeader, Pager } from './TableControls'
import { Treemap } from './Treemap'
import { TONE_WORD } from './treemapLayout'
import { useClientTable } from './useClientTable'

const POLL_INTERVAL_MS = 2500
const CHANGE_GLYPH = { up: '▲', down: '▼', flat: '–' }
const TABS = [
  { key: 'map', label: 'Map' },
  { key: 'sentiment', label: 'Sentiment' },
  { key: 'table', label: 'Industries' },
]

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
  const [active, setActive] = useState('map')
  const tabRefs = useRef({})
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

  function focusTab(key) {
    setActive(key)
    tabRefs.current[key]?.focus()
  }

  function onTabKeyDown(event) {
    const index = TABS.findIndex((tab) => tab.key === active)
    if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
      event.preventDefault()
      const step = event.key === 'ArrowRight' ? 1 : -1
      focusTab(TABS[(index + step + TABS.length) % TABS.length].key)
    } else if (event.key === 'Home') {
      event.preventDefault()
      focusTab(TABS[0].key)
    } else if (event.key === 'End') {
      event.preventDefault()
      focusTab(TABS[TABS.length - 1].key)
    }
  }

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

          <article className="panel">
            <div className="tabbar">
              <div className="tabs" role="tablist" aria-label="Industry sections" onKeyDown={onTabKeyDown}>
                {TABS.map((tab) => {
                  const selected = tab.key === active
                  return (
                    <button
                      key={tab.key}
                      type="button"
                      role="tab"
                      id={`industriestab-${tab.key}`}
                      aria-selected={selected}
                      aria-controls={`industriespanel-${tab.key}`}
                      tabIndex={selected ? 0 : -1}
                      ref={(element) => {
                        tabRefs.current[tab.key] = element
                      }}
                      className={`tab${selected ? ' is-active' : ''}`}
                      onClick={() => setActive(tab.key)}
                    >
                      {tab.label}
                    </button>
                  )
                })}
              </div>
            </div>

            <div
              className="tabpanel"
              role="tabpanel"
              id={`industriespanel-${active}`}
              aria-labelledby={`industriestab-${active}`}
            >
              {active === 'map' ? (
                treemapItems.length === 0 ? (
                  <p className="note">Seed the market to see industries.</p>
                ) : (
                  <>
                    <p className="tabpanel-meta">
                      {treemapItems.length} industries · {formatCompactMoney(totalWorth)}
                    </p>
                    <div className="map-layout">
                      <Treemap
                        items={treemapItems}
                        onSelect={(industryId) => navigate(`/industries/${industryId}`)}
                        formatValue={formatCompactMoney}
                        formatChange={formatSentimentChange}
                        ariaLabel="Industries by total worth"
                      />
                    </div>
                  </>
                )
              ) : null}

              {active === 'sentiment' ? (
                sentimentSeries.length === 0 ? (
                  <p className="note">No sentiment history has been recorded yet.</p>
                ) : (
                  <>
                    <p className="tabpanel-meta">{formatInt(sentimentSeries.length)} series</p>
                    <MultiLineChart
                      series={sentimentSeries}
                      formatValue={formatSentiment}
                      label="Sentiment history for all industries"
                    />
                  </>
                )
              ) : null}

              {active === 'table' ? (
                rows.length === 0 ? (
                  <p className="note">No industries yet.</p>
                ) : (
                  <>
                    <p className="tabpanel-meta">{formatInt(rows.length)} industries</p>
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
                )
              ) : null}
            </div>
          </article>
        </>
      )}
    </main>
  )
}

export default IndustriesPage
