import { useCallback, useEffect, useState } from 'react'
import './App.css'
import { api } from './api'
import { formatInt } from './format'
import { Panel } from './Panel'
import { NewsImpact } from './NewsImpact'
import { newsCategoryStyle, portfolioAuditSummaryId } from './newsCategory'
import { NewsModal } from './NewsModal'
import { AddNewsModal } from './AddNewsModal'
import { useFitPageSize } from './useFitPageSize'
import { PortfolioAuditSummaryModal } from './PortfolioAuditSummaryModal'

const POLL_INTERVAL_MS = 2500

export function NewsFeedPost({ post, onSelectNews, onSelectPortfolioAuditSummary }) {
  const category = newsCategoryStyle(post.category)
  const summaryId = portfolioAuditSummaryId(post)
  const openPost = () => {
    if (summaryId != null) {
      onSelectPortfolioAuditSummary(summaryId)
    } else {
      onSelectNews(post)
    }
  }

  return (
    <article className={category ? `map-news ${category.className}` : 'map-news'}>
      <div className="map-news-head">
        <span className="map-news-label">{category ? category.label : 'Newswire'}</span>
        <span className="map-news-age num">cycle {formatInt(post.publishedInCycleNumber)}</span>
      </div>
      <button
        type="button"
        className="news-feed-title"
        data-portfolio-audit-summary-id={summaryId ?? undefined}
        onClick={openPost}
        title={`Open ${post.title}`}
      >
        {post.title}
      </button>
      <p className="map-news-body">{post.content}</p>
      <NewsImpact post={post} />
    </article>
  )
}

export function NewsSelectionModal({
  selectedNews,
  selectedPortfolioAuditSummaryId,
  onCloseNews,
  onClosePortfolioAuditSummary,
}) {
  if (selectedPortfolioAuditSummaryId != null) {
    return (
      <PortfolioAuditSummaryModal
        summaryId={selectedPortfolioAuditSummaryId}
        onClose={onClosePortfolioAuditSummary}
      />
    )
  }
  return selectedNews ? <NewsModal post={selectedNews} onClose={onCloseNews} /> : null
}

// Full newswire archive in a paginated table. Structured portfolio audits open their immutable summary while
// ordinary headlines retain the post-detail dialog; paging stays on the server because news grows without bound.
function NewsPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [data, setData] = useState(null)
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState(null)
  const [selectedPortfolioAuditSummaryId, setSelectedPortfolioAuditSummaryId] = useState(null)
  const [companies, setCompanies] = useState([])
  const [adding, setAdding] = useState(false)
  const [pageSize, feedRef] = useFitPageSize({ rowSelector: '.map-news', headerSelector: null })

  const loadAll = useCallback(async () => {
    try {
      const [result, companyData] = await Promise.all([api.getNewsPaged(page, pageSize), api.getCompanies()])
      // A resize can shrink the page size (or a live refresh the total) below the current page; snap back.
      const resultPageCount = Math.max(1, Math.ceil((result?.total ?? 0) / pageSize))
      if (page > resultPageCount) {
        setPage(resultPageCount)
        return
      }
      setData(result)
      setCompanies(companyData)
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [page, pageSize])

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
  const pageCount = Math.max(1, Math.ceil(total / pageSize))

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
                  <div className="news-feed" ref={feedRef}>
                    {items.map((post) => (
                      <NewsFeedPost
                        key={post.id}
                        post={post}
                        onSelectNews={(nextPost) => {
                          setSelectedPortfolioAuditSummaryId(null)
                          setSelected(nextPost)
                        }}
                        onSelectPortfolioAuditSummary={(summaryId) => {
                          setSelected(null)
                          setSelectedPortfolioAuditSummaryId(summaryId)
                        }}
                      />
                    ))}
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

      <NewsSelectionModal
        selectedNews={selected}
        selectedPortfolioAuditSummaryId={selectedPortfolioAuditSummaryId}
        onCloseNews={() => setSelected(null)}
        onClosePortfolioAuditSummary={() => setSelectedPortfolioAuditSummaryId(null)}
      />
      {adding ? (
        <AddNewsModal companies={companies} onClose={() => setAdding(false)} onPublished={loadAll} />
      ) : null}
    </>
  )
}

export default NewsPage
