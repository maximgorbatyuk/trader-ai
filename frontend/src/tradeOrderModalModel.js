import { luldPresentation } from './marketAccounting.js'
import { classifyOrderPrice, orderPriceBounds } from './orderPriceRange.js'

const HISTORY_WINDOW = 48

export const TRADE_QUANTITY_PRESETS = [
  { label: '1%', value: 0.01 },
  { label: '5%', value: 0.05 },
  { label: '10%', value: 0.1 },
  { label: '25%', value: 0.25 },
  { label: '50%', value: 0.5 },
  { label: '75%', value: 0.75 },
  { label: '100%', value: 1 },
]

export function recentPriceValues(snapshots) {
  return snapshots.slice(-HISTORY_WINDOW).map((snapshot) => snapshot.price)
}

export function recentSentimentValues(snapshots) {
  return snapshots.slice(-HISTORY_WINDOW).map((snapshot) => snapshot.sentimentValue)
}

export function tradeOrderAvailability({ actorId, orderParticipantId, remaining, price, company }) {
  if (actorId == null) {
    return { eligible: false, reason: 'Select a player or managed fund to accept this order.' }
  }
  if (orderParticipantId === actorId) {
    return { eligible: false, reason: 'You cannot accept your own order.' }
  }
  if (Number(remaining) <= 0) {
    return { eligible: false, reason: 'This order has no remaining shares.' }
  }

  const luld = luldPresentation(company?.luldState)
  if (luld.orderEntryDisabled) {
    return { eligible: false, reason: `Order entry is disabled during ${luld.label}.` }
  }

  const unitPrice = Number(price)
  if (!(unitPrice > 0)) {
    return { eligible: false, reason: 'This order has an invalid price.' }
  }

  const bounds = orderPriceBounds(company)
  const placement = classifyOrderPrice(price, bounds)
  if (placement === 'waiting') {
    return { eligible: false, reason: 'This order is waiting outside the executable price band.' }
  }
  if (placement === 'outside') {
    return { eligible: false, reason: 'This order is outside the allowed price range.' }
  }
  const outsideLegacyBand =
    !bounds.available &&
    typeof company?.lowerBandPrice === 'number' &&
    typeof company?.upperBandPrice === 'number' &&
    (unitPrice < company.lowerBandPrice || unitPrice > company.upperBandPrice)
  if (outsideLegacyBand) {
    return { eligible: false, reason: 'This order is waiting outside the executable price band.' }
  }

  return { eligible: true, reason: null }
}

export function tradeOrderEligibility(options) {
  const availability = tradeOrderAvailability(options)
  if (!availability.eligible) return availability

  const { side, quantity, price, ownedShares, buyingPower, remaining } = options
  const qty = Number(quantity)
  const unitPrice = Number(price)
  if (!Number.isInteger(qty) || qty <= 0 || !(unitPrice > 0)) {
    return { eligible: false, reason: 'Enter a valid quantity and price.' }
  }
  if (qty > Number(remaining)) {
    return { eligible: false, reason: 'Quantity exceeds the remaining order size.' }
  }
  if (side === 'Sell' && qty > Number(ownedShares ?? 0)) {
    return { eligible: false, reason: 'Insufficient shares for this sell order.' }
  }
  if (side === 'Buy' && qty * unitPrice > Number(buyingPower ?? 0)) {
    return { eligible: false, reason: 'Insufficient margin buying power.' }
  }
  return { eligible: true, reason: null }
}
