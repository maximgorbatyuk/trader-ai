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
