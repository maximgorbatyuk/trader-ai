import { RATING_GLYPH, RATING_LABEL, RATING_TAG_CLASS, ratingImpactLabel } from './format'

// Text, glyph, and signed impact keep positive and risk verdicts understandable without relying on hue.
export function RatingBadge({ rating, impactPercent }) {
  if (!rating) {
    return <span className="muted-sub">—</span>
  }

  const label = RATING_LABEL[rating] ?? rating
  const cls = RATING_TAG_CLASS[rating] ?? ''
  const glyph = RATING_GLYPH[rating]
  const impact = ratingImpactLabel(rating, impactPercent)

  return (
    <span className={`tag ${cls}`} data-rating={rating}>
      {glyph ? <span className="rating-glyph" aria-hidden="true">{glyph}</span> : null}
      {label}{impact}
    </span>
  )
}
