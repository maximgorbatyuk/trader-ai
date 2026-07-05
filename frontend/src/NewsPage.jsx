import { useCallback, useEffect, useState } from 'react'
import './App.css'
import { api } from './api'
import { formatInt } from './format'
import { Panel } from './Panel'
import { NewsImpact } from './NewsImpact'
import { NewsModal } from './NewsModal'
import { AddNewsModal } from './AddNewsModal'

const POLL_INTERVAL_MS = 2500
const PAGE_SIZE = 20

// Full newswire archive in a paginated table. Each headline opens a modal with the post's body, impact, and
// the industries it moved, and the composer for a manual post lives here too. News grows without bound, so
// paging happens on the server.
function NewsPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [data, setData] = useState(null)
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState(null)
  const [companies, setCompanies] = useState([])
  const [adding, setAdding] = useState(false)

  const loadAll = useCallback(async () => {
    try {
      const [result, companyData] = await Promise.all([api.getNewsPaged(page, PAGE_SIZE), api.getCompanies()])
      setData(result)
      setCompanies(companyData)
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

  const total = data?.total ?? 0
  const items = data?.items ?? []
  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE))

  return (
    <>
      <main className="main">
        {!ready ? (
          <section className="placeholder" aria-busy="true">
            <span className="spinner" aria-hidden="true" />
            <p>Loading news…</p>
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
              title="News"
              count={`${formatInt(total)}`}
              className="panel-holdings"
              headerExtra={
                <button type="button" className="btn select-sm" onClick={() => setAdding(true)}>
                  + Add news
                </button>
              }
            >
              {items.length === 0 ? (
                <p className="note">No news has been published yet.</p>
              ) : (
                <>
                  <div className="tbl-scroll">
                    <table className="tbl">
                      <thead>
                        <tr>
                          <th scope="col">Name</th>
                          <th scope="col" className="ta-r">
                            Impact
                          </th>
                        </tr>
                      </thead>
                      <tbody>
                        {items.map((post) => (
                          <tr key={post.id}>
                            <th scope="row">
                              <button
                                type="button"
                                className="cell-name-btn"
                                onClick={() => setSelected(post)}
                                title={`Open ${post.title}`}
                              >
                                {post.title}
                              </button>
                            </th>
                            <td className="ta-r">
                              <NewsImpact post={post} />
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

      {selected ? <NewsModal post={selected} onClose={() => setSelected(null)} /> : null}
      {adding ? (
        <AddNewsModal companies={companies} onClose={() => setAdding(false)} onPublished={loadAll} />
      ) : null}
    </>
  )
}

export default NewsPage
