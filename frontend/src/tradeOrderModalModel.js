import { luldPresentation } from './marketAccounting.js'

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

export function tradeOrderEligibility({ side, quantity, price, ownedShares, buyingPower, luldState }) {
  const luld = luldPresentation(luldState)
  if (luld.orderEntryDisabled) {
    return { eligible: false, reason: `Order entry is disabled during ${luld.label}.` }
  }

  const qty = Number(quantity)
  const unitPrice = Number(price)
  if (!Number.isInteger(qty) || qty <= 0 || !(unitPrice > 0)) {
    return { eligible: false, reason: 'Enter a valid quantity and price.' }
  }
  if (side === 'Sell' && qty > Number(ownedShares ?? 0)) {
    return { eligible: false, reason: 'Insufficient shares for this sell order.' }
  }
  if (side === 'Buy' && qty * unitPrice > Number(buyingPower ?? 0)) {
    return { eligible: false, reason: 'Insufficient margin buying power.' }
  }
  return { eligible: true, reason: null }
}
