// Shared, server-owned notion of where a limit price rests: the executable band that matching crosses, and the
// wider allowed range an order may wait in. Both come straight off a company response, so order entry and the
// order book classify prices the same way the backend does. This is presentation only — the server still decides.

function numberOrNull(value) {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

export function orderPriceBounds(company) {
  const activeLower = numberOrNull(company?.lowerBandPrice)
  const activeUpper = numberOrNull(company?.upperBandPrice)
  const allowedMin = numberOrNull(company?.minimumOrderPrice)
  const allowedMax = numberOrNull(company?.maximumOrderPrice)
  const available = activeLower != null && activeUpper != null && allowedMin != null && allowedMax != null
  return { available, activeLower, activeUpper, allowedMin, allowedMax }
}

// 'executable' rests inside the band; 'waiting' is inside the allowed range but outside the band; 'outside' is
// beyond the allowed range; 'invalid' is a non-positive price; 'unavailable' means the company exposed no bounds.
export function classifyOrderPrice(price, bounds) {
  if (!bounds.available) return 'unavailable'
  const value = Number(price)
  if (!(value > 0)) return 'invalid'
  if (value < bounds.allowedMin || value > bounds.allowedMax) return 'outside'
  if (value < bounds.activeLower || value > bounds.activeUpper) return 'waiting'
  return 'executable'
}

// Presets span the whole allowed range: its edges, the band edges, and the current market price in the middle.
export function orderPricePresets(company) {
  const bounds = orderPriceBounds(company)
  if (!bounds.available) return []
  const market = numberOrNull(company?.currentPrice)
  return [
    { label: 'Range low', value: bounds.allowedMin },
    { label: 'Band low', value: bounds.activeLower },
    ...(market != null ? [{ label: 'Market', value: market }] : []),
    { label: 'Band high', value: bounds.activeUpper },
    { label: 'Range high', value: bounds.allowedMax },
  ]
}
