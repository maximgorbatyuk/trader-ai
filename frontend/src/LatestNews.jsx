import { NewsImpact } from './NewsImpact'
import { newsCategoryStyle } from './newsCategory'

// The most recent posts rendered as compact cards, newest first. Each card shows its market effect through the
// shared NewsImpact, so a company-scoped post links straight to that company when onSelectCompany is given.
// Used both under the market map and on the trade-market page.
export function LatestNews({ news, currentCycleNumber, onSelectCompany, count = 2 }) {
  const items = (news ?? []).slice(0, count)

  if (items.length === 0) {
    return (
      <div className="map-news map-news-empty">
        <span className="map-news-label">Latest news</span>
        <span className="map-news-hint">No market news yet.</span>
      </div>
    )
  }

  return (
    <div className="map-news-list">
      {items.map((post) => {
        const cyclesAgo = Math.max(0, currentCycleNumber - post.publishedInCycleNumber)
        const ageLabel = cyclesAgo === 0 ? 'this cycle' : `${cyclesAgo} cycle${cyclesAgo === 1 ? '' : 's'} ago`
        const category = newsCategoryStyle(post.category)
        return (
          <div className={category ? `map-news ${category.className}` : 'map-news'} key={post.id}>
            <div className="map-news-head">
              <span className="map-news-label">{category ? category.label : 'Latest news'}</span>
              <span className="map-news-age num">{ageLabel}</span>
            </div>
            <p className="map-news-title">{post.title}</p>
            <p className="map-news-body">{post.content}</p>
            <NewsImpact post={post} onSelectCompany={onSelectCompany} />
          </div>
        )
      })}
    </div>
  )
}
