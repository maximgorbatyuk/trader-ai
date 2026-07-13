const LULD_PRESENTATION = {
  Normal: {
    label: 'Normal',
    indicator: '✓',
    tone: 'up',
    orderEntryDisabled: false,
    executionNote: 'Continuous trading is active.',
  },
  LimitState: {
    label: 'Limit State',
    indicator: '!',
    tone: 'attention',
    orderEntryDisabled: true,
    executionNote: 'Orders remain open and cancellable; continuous matching is paused.',
  },
  TradingPause: {
    label: 'Trading Pause',
    indicator: '■',
    tone: 'down',
    orderEntryDisabled: true,
    executionNote: 'Orders remain open and cancellable while trading is paused.',
  },
  Reopening: {
    label: 'Reopening',
    indicator: '↻',
    tone: 'attention',
    orderEntryDisabled: true,
    executionNote: 'Resting orders may execute in the reopening auction; new order entry remains disabled.',
  },
}

export function cashSettlement(total, settled) {
  const totalCash = Number(total ?? 0)
  const settledCash = Number(settled ?? 0)
  return { total: totalCash, settled: settledCash, pending: totalCash - settledCash }
}

export function quantitySettlement(economic, settled) {
  const economicQuantity = Number(economic ?? 0)
  const settledQuantity = Number(settled ?? 0)
  return { economic: economicQuantity, settled: settledQuantity, pending: economicQuantity - settledQuantity }
}

export function settlementLabel(settlement) {
  if (!settlement) return '—'
  if (settlement.status === 'Pending') {
    const lag = Number(settlement.dueDayNumber) - Number(settlement.tradeDayNumber)
    const lagLabel = Number.isFinite(lag) && lag > 0 ? `T+${lag}` : 'Pending'
    return `Pending · ${lagLabel} · due Day ${settlement.dueDayNumber}`
  }
  return settlement.dueDayNumber == null ? settlement.status ?? '—' : `${settlement.status ?? 'Settled'} · Day ${settlement.dueDayNumber}`
}

export function luldPresentation(state) {
  return LULD_PRESENTATION[state] ?? LULD_PRESENTATION.Normal
}
