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

// Risk-rating labels and tag classes. Severity escalates through non-market hues (calm blue → amber → magenta)
// so the market's reserved green/red are untouched; the text label carries the meaning without relying on hue.
export const RATING_LABEL = {
  Low: 'Low risk',
  High: 'High risk',
  Extra: 'Extra risk',
}

export const RATING_TAG_CLASS = {
  Low: 'tag-rating-low',
  High: 'tag-rating-high',
  Extra: 'tag-rating-extra',
}
