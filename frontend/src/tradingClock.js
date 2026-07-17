const CLOCK_FIELDS = [
  'tradingDayNumber',
  'tradingSessionState',
  'tradingCycleNumber',
  'remainingTradingCycles',
  'remainingPhaseSeconds',
  'tradingCycleSeconds',
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
    receivedAtMs,
  }
}

// While the market runs, the countdown interpolates the existing snapshot smoothly between polls rather than
// re-seeding on every poll, which would snap it back up because the server's remaining seconds is a per-cycle
// step value. Re-sync whenever the server's cycle advances, at a day or Trading/Break boundary, or when the
// market stops, so client interpolation can never drift ahead of the backend's real per-cycle pace.
export function shouldKeepTradingClock(previous, next) {
  if (!previous || !next || next.marketStatus !== 'Running') return false
  return (
    previous.marketStatus === next.marketStatus &&
    previous.tradingDayNumber === next.tradingDayNumber &&
    previous.tradingSessionState === next.tradingSessionState &&
    previous.tradingCycleNumber === next.tradingCycleNumber
  )
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
  }
}
