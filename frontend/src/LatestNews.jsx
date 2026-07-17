import { Link } from 'react-router-dom'
import { NewsImpact } from './NewsImpact'
import { newsCategoryStyle } from './newsCategory'

const RECENT_EVENT_CYCLES = 15

function eventImpactRange(industries, sign) {
  const percents = industries.map((industry) => Number(industry.impactPercent))
  if (percents.length === 0) return null
  const min = Math.min(...percents)
  const max = Math.max(...percents)
  return min === max ? `${sign}${max.toFixed(1)}%` : `${sign}${min.toFixed(1)}% to ${sign}${max.toFixed(1)}%`
}

function latestRecentEvent(crises, scienceInvestigations, currentCycleNumber) {
  const candidates = [
    crises?.[0] ? { kind: 'crisis', event: crises[0] } : null,
    scienceInvestigations?.[0] ? { kind: 'science', event: scienceInvestigations[0] } : null,
  ].filter(Boolean)

  return candidates
    .filter(({ event }) =>
      typeof currentCycleNumber !== 'number' ||
      currentCycleNumber - event.triggeredInCycleNumber <= RECENT_EVENT_CYCLES)
    .sort((a, b) => b.event.triggeredInCycleNumber - a.event.triggeredInCycleNumber)[0] ?? null
}

function cycleAge(cycleNumber, currentCycleNumber) {
  const cyclesAgo = Math.max(0, (currentCycleNumber ?? cycleNumber) - cycleNumber)
  return cyclesAgo === 0 ? 'this cycle' : `${cyclesAgo} cycle${cyclesAgo === 1 ? '' : 's'} ago`
}

// A recent crisis or science event keeps one of the two map-feed slots so it remains visible without a
// separate dashboard banner; ordinary news fills both slots when no market event is recent.
export function LatestNews({
  news,
  currentCycleNumber,
  onSelectCompany,
  count = 2,
  crises = [],
  scienceInvestigations = [],
}) {
  const recentEvent = count > 0 ? latestRecentEvent(crises, scienceInvestigations, currentCycleNumber) : null
  const items = (news ?? []).slice(0, recentEvent ? Math.max(0, count - 1) : count)
  let eventCard = null

  if (recentEvent) {
    const { event, kind } = recentEvent
    const sectorCount = event.industries.length
    const sectorText = `${sectorCount} ${sectorCount === 1 ? 'sector' : 'sectors'}`
    const impact = eventImpactRange(event.industries, kind === 'crisis' ? '−' : '+')
    const label = kind === 'crisis' ? `⚠ ${event.scope} crisis` : '🔬 Science breakthrough'
    const meta = [sectorText, impact, `cycle ${event.triggeredInCycleNumber}`].filter(Boolean).join(' · ')

    eventCard = (
      <div className={`map-news news-${kind}`} key={`${kind}-${event.id}`}>
        <div className="map-news-head">
          <span className="map-news-label">{label}</span>
          <span className="map-news-age num">{cycleAge(event.triggeredInCycleNumber, currentCycleNumber)}</span>
        </div>
        <p className="map-news-title">
          {kind === 'crisis' ? <Link to={`/crises/${event.id}`}>{event.title}</Link> : event.title}
        </p>
        <p className="map-news-body">{event.content}</p>
        <span className="map-news-impact num">{meta}</span>
      </div>
    )
  }

  if (!recentEvent && items.length === 0) {
    return (
      <div className="map-news map-news-empty">
        <span className="map-news-label">Latest news</span>
        <span className="map-news-hint">No market news yet.</span>
      </div>
    )
  }

  return (
    <div className="map-news-list">
      {eventCard}
      {items.map((post) => {
        const category = newsCategoryStyle(post.category)
        return (
          <div className={category ? `map-news ${category.className}` : 'map-news'} key={post.id}>
            <div className="map-news-head">
              <span className="map-news-label">{category ? category.label : 'Latest news'}</span>
              <span className="map-news-age num">{cycleAge(post.publishedInCycleNumber, currentCycleNumber)}</span>
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
