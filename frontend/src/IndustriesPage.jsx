import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatCompactMoney, formatInt, formatMoney, toneOf } from './format'
import { Panel } from './Panel'
import { Treemap } from './Treemap'
import { formatPct, TONE_WORD } from './treemapLayout'
import { IndustryModal } from './IndustryModal'

const POLL_INTERVAL_MS = 2500
const CHANGE_GLYPH = { up: '▲', down: '▼', flat: '–' }

// Industry-level view of the market: a treemap sized by each industry's total worth, a full table below it,
// and a per-industry company modal. Everything is aggregated in the browser from the companies list; the
// last-cycle change is the same forward-only capitalisation diff the dashboard market map uses (0 until a
// cycle advances after load), so no dedicated backend endpoint is needed.
function IndustriesPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [companies, setCompanies] = useState([])
  const [cycleNumber, setCycleNumber] = useState(null)
  const [selected, setSelected] = useState(null)
  // Previous-cycle capitalisation per company and per industry, anchored to the cycle so the change holds
  // through every poll of a cycle and only recomputes when a new cycle advances.
  const [snap, setSnap] = useState({
    cycle: null,
    industryCap: new Map(),
    companyChange: new Map(),
    industryChange: new Map(),
  })

  const loadAll = useCallback(async () => {
    try {
      const [companyData, marketData] = await Promise.all([api.getCompanies(), api.getMarket()])
      setCompanies(companyData ?? [])
      setCycleNumber(marketData?.currentCycleNumber ?? null)
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

  if (snap.cycle !== cycleNumber) {
    const prevIndustry = snap.industryCap
    const prevCompanyCap = snap.companyCapForChange ?? new Map()
    const companyCap = new Map()
    const industryCap = new Map()
    for (const company of companies) {
      const cap = company.issuedSharesCount * (company.currentPrice ?? 0)
      companyCap.set(company.id, cap)
      industryCap.set(company.industryId, (industryCap.get(company.industryId) ?? 0) + cap)
    }
    const companyChange = new Map()
    for (const [id, cap] of companyCap) {
      const prev = prevCompanyCap.get(id)
      companyChange.set(id, prev > 0 ? (cap - prev) / prev : 0)
    }
    const industryChange = new Map()
    for (const [id, cap] of industryCap) {
      const prev = prevIndustry.get(id)
      industryChange.set(id, prev > 0 ? (cap - prev) / prev : 0)
    }
    setSnap({ cycle: cycleNumber, industryCap, companyCapForChange: companyCap, companyChange, industryChange })
  }

  // Aggregate companies into their industries.
  const byIndustry = new Map()
  for (const company of companies) {
    const cap = company.issuedSharesCount * (company.currentPrice ?? 0)
    const entry = byIndustry.get(company.industryId) ?? {
      id: company.industryId,
      name: company.industryName ?? '—',
      count: 0,
      totalWorth: 0,
    }
    entry.count += 1
    entry.totalWorth += cap
    byIndustry.set(company.industryId, entry)
  }

  const industries = [...byIndustry.values()]
    .map((entry) => ({ ...entry, changePct: snap.industryChange.get(entry.id) ?? 0 }))
    .sort((a, b) => b.totalWorth - a.totalWorth)

  const treemapItems = industries
    .filter((industry) => industry.totalWorth > 0)
    .map((industry) => ({
      id: industry.id,
      label: industry.name,
      value: industry.totalWorth,
      changePct: industry.changePct,
      title: `${industry.name} · ${formatCompactMoney(industry.totalWorth)} · ${industry.count} companies · ${formatPct(industry.changePct)}`,
      ariaLabel: `${industry.name}, ${formatCompactMoney(industry.totalWorth)} total worth, ${industry.count} companies, ${TONE_WORD[toneOf(industry.changePct)]} ${formatPct(industry.changePct)}. Open companies.`,
    }))

  const selectedIndustry = selected ? industries.find((industry) => industry.id === selected) ?? null : null
  const totalWorth = industries.reduce((sum, industry) => sum + industry.totalWorth, 0)

  return (
    <div className="app">
      <header className="topbar">
        <Link className="brand" to="/" aria-label="Back to the Trader AI dashboard">
          <span className="brand-mark" aria-hidden="true">
            TA
          </span>
          <span className="brand-name">Trader&nbsp;AI</span>
          <span className="brand-tag" aria-hidden="true">
            Industries
          </span>
        </Link>
        <Link className="btn" to="/">
          ← Dashboard
        </Link>
      </header>

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
                <Treemap
                  items={treemapItems}
                  onSelect={setSelected}
                  formatValue={formatCompactMoney}
                  ariaLabel="Industries by total worth"
                />
              )}
            </Panel>

            <Panel title="Industries" count={`${formatInt(industries.length)}`} className="panel-holdings">
              {industries.length === 0 ? (
                <p className="note">No industries yet.</p>
              ) : (
                <div className="tbl-wrap">
                  <table className="tbl">
                    <thead>
                      <tr>
                        <th scope="col">Name</th>
                        <th scope="col" className="ta-r">
                          Companies
                        </th>
                        <th scope="col" className="ta-r">
                          Total worth
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {industries.map((industry) => {
                        const tone = toneOf(industry.changePct)
                        return (
                          <tr key={industry.id}>
                            <th scope="row" className="cell-ellipsis">
                              <button
                                type="button"
                                className="cell-name-btn"
                                onClick={() => setSelected(industry.id)}
                                title={`Open ${industry.name} companies`}
                              >
                                {industry.name}
                              </button>
                            </th>
                            <td className="num ta-r">{formatInt(industry.count)}</td>
                            <td className="num ta-r">
                              {formatMoney(industry.totalWorth)}
                              <span className={`book-diff num tone-${tone}`}>
                                <span aria-hidden="true">{CHANGE_GLYPH[tone]} </span>
                                {formatPct(industry.changePct)}
                              </span>
                            </td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </Panel>
          </>
        )}
      </main>

      {selectedIndustry ? (
        <IndustryModal
          industry={selectedIndustry}
          companies={companies}
          companyChangeById={snap.companyChange}
          onClose={() => setSelected(null)}
        />
      ) : null}
    </div>
  )
}

export default IndustriesPage
