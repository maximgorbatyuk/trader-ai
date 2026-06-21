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

// Market semantics for a delta: up/down/flat, used to pick the green/red/neutral tone class.
export function toneOf(value) {
  if (typeof value !== 'number' || value === 0) return 'flat'
  return value > 0 ? 'up' : 'down'
}
