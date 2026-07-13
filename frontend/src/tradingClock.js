const CLOCK_FIELDS = [
  'tradingDayNumber',
  'tradingSessionState',
  'tradingCycleNumber',
  'remainingTradingCycles',
  'remainingPhaseSeconds',
  'tradingCycleSeconds',
  'nextStepMeaning',
]

export function createTradingClock(market, receivedAtMs = Date.now()) {
  if (!market || CLOCK_FIELDS.some((field) => market[field] == null)) return null

  return {
    marketStatus: market.status,
    tradingDayNumber: market.tradingDayNumber,
    tradingSessionState: market.tradingSessionState,
    tradingCycleNumber: market.tradingCycleNumber,
    remainingTradingCycles: market.remainingTradingCycles,
    remainingPhaseSeconds: Math.max(0, market.remainingPhaseSeconds),
    tradingCycleSeconds: market.tradingCycleSeconds,
    nextStepMeaning: market.nextStepMeaning,
    receivedAtMs,
  }
}

export function interpolateTradingClock(snapshot, nowMs = Date.now()) {
  if (!snapshot) return null

  const elapsedSeconds =
    snapshot.marketStatus === 'Running' ? Math.max(0, Math.floor((nowMs - snapshot.receivedAtMs) / 1_000)) : 0
  const elapsedCycles =
    snapshot.marketStatus === 'Running' &&
    snapshot.tradingSessionState === 'Trading' &&
    snapshot.tradingCycleSeconds > 0
      ? Math.min(snapshot.remainingTradingCycles, Math.floor(elapsedSeconds / snapshot.tradingCycleSeconds))
      : 0

  return {
    ...snapshot,
    tradingCycleNumber: snapshot.tradingCycleNumber + elapsedCycles,
    remainingTradingCycles: snapshot.remainingTradingCycles - elapsedCycles,
    remainingPhaseSeconds: Math.max(0, snapshot.remainingPhaseSeconds - elapsedSeconds),
  }
}

export function formatTradingClock(clock) {
  if (!clock) return null

  const totalCycles = clock.tradingCycleNumber + clock.remainingTradingCycles
  const minutes = Math.floor(clock.remainingPhaseSeconds / 60)
  const seconds = clock.remainingPhaseSeconds % 60

  return {
    dayPhaseLabel: `Day ${clock.tradingDayNumber} · ${clock.tradingSessionState}`,
    cycleLabel: `Cycle ${clock.tradingCycleNumber}/${totalCycles} · ${clock.remainingTradingCycles} left`,
    timeLabel: `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')} left`,
    nextStepTitle: clock.nextStepMeaning,
  }
}
