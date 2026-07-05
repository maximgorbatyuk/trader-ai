import { useCallback, useEffect, useState } from 'react'
import './App.css'
import { api } from './api'
import { formatInt, formatMoney } from './format'
import { Panel } from './Panel'
import { TemperamentTag } from './TemperamentTag'

const POLL_INTERVAL_MS = 2500
const PAGE_SIZE = 20

// Roster of collective funds that have unwound. They are dropped from the live Traders list once closed, so this
// page is where their history stays visible; the table is paginated newest-first and polls its current page.
function ClosedFundsPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [funds, setFunds] = useState(null)
  const [page, setPage] = useState(1)

  const loadAll = useCallback(async () => {
    try {
      const data = await api.getClosedFunds(page, PAGE_SIZE)
      setFunds(data)
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [page])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll])

  const total = funds?.total ?? 0
  const items = funds?.items ?? []
  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE))

  return (
    <main className="main">
      {!ready ? (
        <section className="placeholder" aria-busy="true">
          <span className="spinner" aria-hidden="true" />
          <p>Loading closed funds…</p>
        </section>
      ) : (
        <>
          {loadError ? (
            <div className="banner" role="alert">
              <strong>Showing last known state.</strong>
              <span>{loadError}</span>
            </div>
          ) : null}

          <Panel title="Closed funds" count={`${formatInt(total)}`} className="panel-holdings">
            {items.length === 0 ? (
              <p className="note">No funds have closed yet.</p>
            ) : (
              <>
                <div className="tbl-scroll">
                  <table className="tbl">
                    <thead>
                      <tr>
                        <th scope="col">Name</th>
                        <th scope="col">Personality</th>
                        <th scope="col" className="ta-r">
                          Peak worth
                        </th>
                        <th scope="col" className="ta-r">
                          Opened
                        </th>
                        <th scope="col" className="ta-r">
                          Closed
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {items.map((fund) => (
                        <tr key={fund.id}>
                          <th scope="row" className="cell-ellipsis">
                            {fund.name}
                          </th>
                          <td>
                            <span className="cell-trader">
                              <TemperamentTag temperament={fund.temperament} type="CollectiveFund" />
                              {fund.riskProfile ? <span className="tag">{fund.riskProfile} risk</span> : null}
                            </span>
                          </td>
                          <td className="num ta-r">{formatMoney(fund.peakNetWorth)}</td>
                          <td className="num ta-r">cycle {formatInt(fund.createdInCycleNumber)}</td>
                          <td className="num ta-r">
                            {fund.closedAt ? new Date(fund.closedAt).toLocaleDateString() : '—'}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                {pageCount > 1 ? (
                  <div className="pager">
                    <button
                      type="button"
                      className="btn"
                      disabled={page <= 1}
                      onClick={() => setPage((value) => value - 1)}
                    >
                      ← Prev
                    </button>
                    <span className="pager-status num">
                      Page {page} / {pageCount}
                    </span>
                    <button
                      type="button"
                      className="btn"
                      disabled={page >= pageCount}
                      onClick={() => setPage((value) => value + 1)}
                    >
                      Next →
                    </button>
                  </div>
                ) : null}
              </>
            )}
          </Panel>
        </>
      )}
    </main>
  )
}

export default ClosedFundsPage
