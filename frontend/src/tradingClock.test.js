import assert from 'node:assert/strict'
import test from 'node:test'

async function loadClock() {
  return import('./tradingClock.js').catch(() => ({}))
}

const tradingMarket = {
  status: 'Running',
  tradingDayNumber: 7,
  tradingSessionState: 'Trading',
  tradingCycleNumber: 84,
  remainingTradingCycles: 126,
  remainingPhaseSeconds: 252,
  tradingCycleSeconds: 2,
  nextStepMeaning: 'Advance one trading cycle',
}

test('formats the trading phase, cycle progress, remaining time, and next step', async () => {
  const { createTradingClock, interpolateTradingClock, formatTradingClock } = await loadClock()
  const snapshot = createTradingClock?.(tradingMarket, 1_000)

  assert.deepEqual(formatTradingClock?.(interpolateTradingClock?.(snapshot, 1_000)), {
    dayPhaseLabel: 'Day 7 · Trading',
    cycleLabel: 'Cycle 84/210 · 126 left',
    timeLabel: '04:12 left',
    nextStepTitle: 'Advance one trading cycle',
  })
})

test('formats the break without advancing the completed trading-cycle count', async () => {
  const { createTradingClock, interpolateTradingClock, formatTradingClock } = await loadClock()
  const snapshot = createTradingClock?.(
    {
      ...tradingMarket,
      tradingSessionState: 'Break',
      tradingCycleNumber: 210,
      remainingTradingCycles: 0,
      remainingPhaseSeconds: 43,
      nextStepMeaning: 'Advance the break countdown by 2 seconds',
    },
    1_000,
  )

  assert.deepEqual(formatTradingClock?.(interpolateTradingClock?.(snapshot, 1_000)), {
    dayPhaseLabel: 'Day 7 · Break',
    cycleLabel: 'Cycle 210/210 · 0 left',
    timeLabel: '00:43 left',
    nextStepTitle: 'Advance the break countdown by 2 seconds',
  })
})

test('interpolates a running countdown from the server snapshot time', async () => {
  const { createTradingClock, interpolateTradingClock } = await loadClock()
  const snapshot = createTradingClock?.(tradingMarket, 1_000)

  const projected = interpolateTradingClock?.(snapshot, 3_500)

  assert.equal(projected?.remainingPhaseSeconds, 250)
  assert.equal(projected?.tradingCycleNumber, 85)
  assert.equal(projected?.remainingTradingCycles, 125)
})

test('freezes interpolation while the market is paused', async () => {
  const { createTradingClock, interpolateTradingClock } = await loadClock()
  const snapshot = createTradingClock?.({ ...tradingMarket, status: 'Paused' }, 1_000)

  assert.equal(interpolateTradingClock?.(snapshot, 12_000)?.remainingPhaseSeconds, 252)
})

test('clamps the countdown at zero', async () => {
  const { createTradingClock, interpolateTradingClock, formatTradingClock } = await loadClock()
  const snapshot = createTradingClock?.({ ...tradingMarket, remainingPhaseSeconds: 1 }, 1_000)
  const clock = interpolateTradingClock?.(snapshot, 12_000)

  assert.equal(clock?.remainingPhaseSeconds, 0)
  assert.equal(formatTradingClock?.(clock)?.timeLabel, '00:00 left')
})

test('resynchronizes to every newer server snapshot', async () => {
  const { createTradingClock, interpolateTradingClock } = await loadClock()
  const oldSnapshot = createTradingClock?.(tradingMarket, 1_000)
  const oldProjection = interpolateTradingClock?.(oldSnapshot, 6_000)
  const correctedSnapshot = createTradingClock?.({ ...tradingMarket, remainingPhaseSeconds: 251 }, 6_000)

  assert.equal(oldProjection?.remainingPhaseSeconds, 247)
  assert.equal(interpolateTradingClock?.(correctedSnapshot, 6_000)?.remainingPhaseSeconds, 251)
})

test('returns no clock until the server supplies the complete trading-clock contract', async () => {
  const { createTradingClock, formatTradingClock } = await loadClock()

  assert.equal(createTradingClock?.({ status: 'Running', tradingDayNumber: 1 }, 1_000), null)
  assert.equal(formatTradingClock?.(null), null)
})

test('keeps the running countdown within a phase but re-syncs across phase boundaries', async () => {
  const { createTradingClock, shouldKeepTradingClock } = await loadClock()
  const previous = createTradingClock?.(tradingMarket, 1_000)

  // Same running phase, later cycle and lower remaining seconds: keep interpolating the existing snapshot.
  const laterInSamePhase = createTradingClock?.(
    { ...tradingMarket, tradingCycleNumber: 90, remainingTradingCycles: 120, remainingPhaseSeconds: 240 },
    5_000,
  )
  assert.equal(shouldKeepTradingClock?.(previous, laterInSamePhase), true)

  // A new trading day, the Trading/Break switch, or a market state change each force a re-sync.
  assert.equal(shouldKeepTradingClock?.(previous, createTradingClock?.({ ...tradingMarket, tradingDayNumber: 8 }, 5_000)), false)
  assert.equal(shouldKeepTradingClock?.(previous, createTradingClock?.({ ...tradingMarket, tradingSessionState: 'Break' }, 5_000)), false)
  assert.equal(shouldKeepTradingClock?.(previous, createTradingClock?.({ ...tradingMarket, status: 'Paused' }, 5_000)), false)
  assert.equal(shouldKeepTradingClock?.(null, laterInSamePhase), false)
})
