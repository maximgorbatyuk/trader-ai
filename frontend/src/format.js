const moneyFormatter = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
})
const intFormatter = new Intl.NumberFormat('en-US')
// Compact notation keeps large market-wide aggregates legible in tight columns; the precise value is
// surfaced separately (a tooltip) where exact figures matter.
const compactMoneyFormatter = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  notation: 'compact',
  maximumFractionDigits: 1,
})

export function formatMoney(value) {
  return typeof value === 'number' ? moneyFormatter.format(value) : '—'
}

export function formatCompactMoney(value) {
  return typeof value === 'number' ? compactMoneyFormatter.format(value) : '—'
}

export function formatInt(value) {
  return typeof value === 'number' ? intFormatter.format(value) : '—'
}

export function formatSigned(value) {
  if (typeof value !== 'number') return '—'
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  return `${sign}${moneyFormatter.format(Math.abs(value))}`
}

export function formatSignedInt(value) {
  if (typeof value !== 'number') return '—'
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  return `${sign}${intFormatter.format(Math.abs(value))}`
}

// Market semantics for a delta: up/down/flat, used to pick the green/red/neutral tone class.
export function toneOf(value) {
  if (typeof value !== 'number' || value === 0) return 'flat'
  return value > 0 ? 'up' : 'down'
}

// Short, column-friendly labels for the participant-type enum, shared by the traders table and its summary
// modal. The trader detail block uses its own longer labels.
export const TRADER_TYPE_LABEL = {
  Individual: 'Individual',
  Company: 'Company',
  AIAgent: 'AI',
  CollectiveFund: 'Fund',
  Player: 'Player',
}

// Color-modifier class per temperament value; hues stay off market green/red, which are reserved for up/down.
export const TEMPERAMENT_TAG_CLASS = {
  Aggressive: 'tag-temperament-aggressive',
  Balanced: 'tag-temperament-balanced',
  Conservative: 'tag-temperament-conservative',
}

// Rating labels and tag classes. Risk severity uses non-market hues, while positive outlooks use the
// reserved market-up tone; every meaning is also carried by text rather than hue alone.
export const RATING_LABEL = {
  ExtraRaisedExpectations: 'Extra raised expectations',
  RaisedExpectations: 'Raised expectations',
  Low: 'Low risk',
  High: 'High risk',
  Extra: 'Extra risk',
}

export const RATING_TAG_CLASS = {
  ExtraRaisedExpectations: 'tag-rating-raised',
  RaisedExpectations: 'tag-rating-raised',
  Low: 'tag-rating-low',
  High: 'tag-rating-high',
  Extra: 'tag-rating-extra',
}

// Friendly labels for AI-call statuses; the label text carries the meaning without relying on color.
export const AI_CALL_STATUS_LABEL = {
  Pending: 'Pending',
  Completed: 'Completed',
  HttpError: 'HTTP error',
  TimedOut: 'Timed out',
  InvalidJson: 'Invalid JSON',
  Cancelled: 'Cancelled',
  Abandoned: 'Abandoned',
  PendingNextDay: 'Pending next day',
}

export function aiCallStatusLabel(status) {
  return AI_CALL_STATUS_LABEL[status] ?? status
}

const RATING_ORDER = { ExtraRaisedExpectations: -2, RaisedExpectations: -1, Low: 0, High: 1, Extra: 2 }

export function ratingTrend(current, previous) {
  if (!current || !previous || !(current in RATING_ORDER) || !(previous in RATING_ORDER)) return null
  if (RATING_ORDER[current] > RATING_ORDER[previous]) return 'worsened'
  if (RATING_ORDER[current] < RATING_ORDER[previous]) return 'improved'
  return null
}

export function ratingImpactLabel(rating, impactPercent) {
  if (typeof impactPercent !== 'number') return ''
  if (rating === 'RaisedExpectations' || rating === 'ExtraRaisedExpectations') {
    return ` +${impactPercent.toFixed(0)}%`
  }
  return rating === 'Extra' ? ` −${impactPercent.toFixed(0)}%` : ''
}

// Renders an AI order's signed price offset so its direction reads without relying on color.
export function formatSignedPercent(value) {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '—'
  const rounded = Number(value.toFixed(2))
  const sign = rounded > 0 ? '+' : rounded < 0 ? '−' : ''
  return `${sign}${Math.abs(rounded)}%`
}
