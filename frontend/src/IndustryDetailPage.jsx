import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt, formatMoney, toneOf } from './format'
import { LineChart } from './LineChart'
import { NewsImpact } from './NewsImpact'
import { NewsModal } from './NewsModal'
import { Panel } from './Panel'

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

// A standalone industry readout, keyed by route id. It polls its own detail, history, news, and live company
// rows so sentiment and the constituent-company table stay truthful while the market advances.
function IndustryDetailPage() {
  const { id } = useParams()
  const industryId = Number(id)
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [detail, setDetail] = useState(null)
  const [history, setHistory] = useState([])
  const [news, setNews] = useState([])
  const [companies, setCompanies] = useState([])
  const [selectedNews, setSelectedNews] = useState(null)
  const [resolvedIndustryId, setResolvedIndustryId] = useState(null)
  const requestIdRef = useRef(0)
  const resolvedIndustryIdRef = useRef(null)
  const navigate = useNavigate()

  const loadAll = useCallback(async () => {
    const requestId = requestIdRef.current + 1
    requestIdRef.current = requestId
    try {
      const [detailData, historyData, newsData, companyData] = await Promise.all([
        api.getIndustry(industryId),
        api.getIndustrySentimentHistory(industryId),
        api.getIndustryNews(industryId),
        api.getCompanies(),
      ])
      if (requestIdRef.current !== requestId) return
      setDetail(detailData)
      setHistory(historyData ?? [])
      setNews(newsData ?? [])
      setCompanies(companyData ?? [])
      setLoadError(null)
      resolvedIndustryIdRef.current = industryId
      setResolvedIndustryId(industryId)
    } catch (error) {
      if (requestIdRef.current !== requestId) return
      if (resolvedIndustryIdRef.current !== industryId) {
        setDetail(null)
      }
      setLoadError(error.message)
      resolvedIndustryIdRef.current = industryId
      setResolvedIndustryId(industryId)
    } finally {
      if (requestIdRef.current === requestId) {
        setReady(true)
      }
    }
  }, [industryId])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
      requestIdRef.current += 1
    }
  }, [loadAll])

  const industryCompanies = useMemo(
    () =>
      companies
        .filter((company) => company.industryId === industryId)
        .map((company) => ({ ...company, netWorth: (company.issuedSharesCount ?? 0) * (company.currentPrice ?? 0) }))
        .sort((a, b) => b.netWorth - a.netWorth),
    [companies, industryId],
  )
  if (!ready || resolvedIndustryId !== industryId) {
    return (
      <main className="main">
        <section className="placeholder" aria-busy="true">
          <span className="spinner" aria-hidden="true" />
          <p>Loading industry…</p>
        </section>
      </main>
    )
  }

  if (detail === null) {
    return (
      <main className="main">
        <div className="banner" role="alert">
          <strong>Couldn&apos;t load this industry.</strong>
          <span>{loadError ?? 'Pick another industry from the Industries page.'}</span>
        </div>
      </main>
    )
  }

  const changeTone = toneOf(detail.lastCycleSentimentChange)
  const chartValues = history.map((point) => point.sentimentValue)

  return (
    <>
      <main className="main">
        <section className="detail-stack" aria-label={`${detail.name} industry details`}>
          {loadError ? (
            <div className="banner" role="alert">
              <strong>Showing last known state.</strong>
              <span>{loadError}</span>
            </div>
          ) : null}

          <section className="command" aria-label="Industry identity">
            <div className="command-id">
              <span className="command-label">Industry</span>
              <h2 className="command-name">{detail.name}</h2>
            </div>
            <dl className="statbar">
              <div className="stat">
                <dt>Total net worth</dt>
                <dd className="num">{formatMoney(detail.totalNetWorth)}</dd>
              </div>
              <div className="stat">
                <dt>Sentiment</dt>
                <dd className="num">{formatSentiment(detail.sentimentValue)}</dd>
              </div>
              <div className="stat">
                <dt>Volatility</dt>
                <dd className="num">{formatDecimal(detail.sentimentVolatility)}</dd>
              </div>
              <div className="stat">
                <dt>Beta</dt>
                <dd className="num">{formatDecimal(detail.sectorBeta)}</dd>
              </div>
              <div className="stat">
                <dt>Last cycle</dt>
                <dd className={`num tone-${changeTone}`}>
                  <span aria-hidden="true">{CHANGE_GLYPH[changeTone]} </span>
                  {formatSentimentChange(detail.lastCycleSentimentChange)}
                </dd>
              </div>
            </dl>
          </section>

          <Panel title="Sentiment history" count={`${formatInt(history.length)} cycles`} className="panel-holdings">
            {chartValues.length === 0 ? (
              <p className="note">No sentiment history has been recorded yet.</p>
            ) : (
              <LineChart values={chartValues} tone={changeTone} formatValue={formatSentiment} label={`${detail.name} sentiment history`} />
            )}
          </Panel>

          <Panel title="Industry news" count={`${formatInt(news.length)}`} className="panel-holdings">
            {news.length === 0 ? (
              <p className="note">No news has affected this industry yet.</p>
            ) : (
              <div className="tbl-wrap">
                <table className="tbl">
                  <thead>
                    <tr>
                      <th scope="col">Headline</th>
                      <th scope="col" className="ta-r">
                        Impact
                      </th>
                      <th scope="col" className="ta-r">
                        Cycle
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {news.map((post) => (
                      <tr key={post.id}>
                        <th scope="row" className="cell-ellipsis">
                          <button
                            type="button"
                            className="cell-name-btn"
                            onClick={() => setSelectedNews(post)}
                            title={`Open ${post.title}`}
                          >
                            {post.title}
                          </button>
                        </th>
                        <td className="ta-r">
                          <NewsImpact post={post} onSelectCompany={(companyId) => navigate(`/companies/${companyId}`)} />
                        </td>
                        <td className="num ta-r">{formatInt(post.publishedInCycleNumber)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Panel>

          <Panel title="Companies" count={`${formatInt(detail.companyCount)}`} className="panel-holdings">
            {industryCompanies.length === 0 ? (
              <p className="note">No live companies are listed in this industry.</p>
            ) : (
              <div className="tbl-wrap">
                <table className="tbl">
                  <thead>
                    <tr>
                      <th scope="col">Company</th>
                      <th scope="col" className="ta-r">
                        Net worth
                      </th>
                      <th scope="col" className="ta-r">
                        Shares
                      </th>
                      <th scope="col" className="ta-r">
                        Current price
                      </th>
                      <th scope="col" className="ta-r">
                        Last cycle
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {industryCompanies.map((company) => {
                      const tone = toneOf(company.priceChangePct)
                      return (
                        <tr key={company.id}>
                          <th scope="row" className="cell-ellipsis">
                            <button
                              type="button"
                              className="cell-name-btn"
                              onClick={() => navigate(`/companies/${company.id}`)}
                              title={`Open ${company.name} details`}
                            >
                              {company.name}
                            </button>
                          </th>
                          <td className="num ta-r">{formatMoney(company.netWorth)}</td>
                          <td className="num ta-r">{formatInt(company.issuedSharesCount)}</td>
                          <td className="num ta-r">{formatMoney(company.currentPrice)}</td>
                          <td className={`num ta-r tone-${tone}`}>
                            <span aria-hidden="true">{CHANGE_GLYPH[tone]} </span>
                            {typeof company.priceChangePct === 'number'
                              ? `${company.priceChangePct > 0 ? '+' : ''}${(company.priceChangePct * 100).toFixed(2)}%`
                              : '—'}
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </Panel>
        </section>
      </main>

      {selectedNews ? <NewsModal post={selectedNews} onClose={() => setSelectedNews(null)} /> : null}
    </>
  )
}

export default IndustryDetailPage
