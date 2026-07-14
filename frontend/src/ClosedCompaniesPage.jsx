import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt, formatMoney } from './format'
import { Panel } from './Panel'

const POLL_INTERVAL_MS = 2500
const PAGE_SIZE = 20

// Archived company view embedded in the Companies roster. It remains independently paged and polled because the
// closed-company response has a different shape from the active roster.
function ClosedCompaniesView({ statusControl }) {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [companies, setCompanies] = useState(null)
  const [page, setPage] = useState(1)

  const loadAll = useCallback(async () => {
    try {
      const data = await api.getClosedCompanies(page, PAGE_SIZE)
      setCompanies(data)
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

  const total = companies?.total ?? 0
  const items = companies?.items ?? []
  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE))

  return (
    <>
      {!ready ? (
        <section className="placeholder" aria-busy="true">
          <span className="spinner" aria-hidden="true" />
          <p>Loading closed companies…</p>
        </section>
      ) : (
        <>
          {loadError ? (
            <div className="banner" role="alert">
              <strong>Showing last known state.</strong>
              <span>{loadError}</span>
            </div>
          ) : null}

          <Panel title="Closed companies" count={`${formatInt(total)}`} className="panel-holdings">
            <div className="roster-toolbar">{statusControl}</div>
            {items.length === 0 ? (
              <p className="note">No companies have been delisted yet.</p>
            ) : (
              <>
                <div className="tbl-wrap">
                  <table className="tbl">
                    <thead>
                      <tr>
                        <th scope="col">Name</th>
                        <th scope="col">Industry</th>
                        <th scope="col" className="ta-r">
                          Issued shares
                        </th>
                        <th scope="col" className="ta-r">
                          Final price
                        </th>
                        <th scope="col" className="ta-r">
                          Listed
                        </th>
                        <th scope="col" className="ta-r">
                          Delisted
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {items.map((company) => (
                        <tr key={company.id}>
                          <th scope="row" className="cell-ellipsis">
                            <Link to={`/companies/${company.id}`}>{company.name}</Link>
                          </th>
                          <td className="cell-ellipsis">{company.industryName ?? '—'}</td>
                          <td className="num ta-r">{formatInt(company.issuedSharesCount)}</td>
                          <td className="num ta-r">{formatMoney(company.finalPrice)}</td>
                          <td className="num ta-r">cycle {formatInt(company.createdInCycleNumber)}</td>
                          <td className="num ta-r">cycle {formatInt(company.closedInCycleNumber)}</td>
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
    </>
  )
}

export default ClosedCompaniesView
