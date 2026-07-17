export function parseCashAdjustment(value) {
  if (String(value).trim() === '') return null
  const amount = Number(value)
  return Number.isFinite(amount) && amount !== 0 ? amount : null
}

export function transferableSettledCash(participant) {
  return Math.max(0, Math.min(participant?.availableBalance ?? 0, participant?.settledCashBalance ?? 0))
}
