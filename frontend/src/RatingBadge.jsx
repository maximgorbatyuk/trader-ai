import { RATING_LABEL, RATING_TAG_CLASS } from './format'

// A risk-rating chip: the text label, plus the price drop for an Extra verdict. Colour is always backed by the
// label so the rating never relies on hue alone.
export function RatingBadge({ rating, impactPercent }) {
  if (!rating) {
    return <span className="muted-sub">—</span>
  }

  const label = RATING_LABEL[rating] ?? rating
  const cls = RATING_TAG_CLASS[rating] ?? ''
  const drop = rating === 'Extra' && typeof impactPercent === 'number' ? ` −${impactPercent.toFixed(0)}%` : ''

  return <span className={`tag ${cls}`}>{label}{drop}</span>
}
