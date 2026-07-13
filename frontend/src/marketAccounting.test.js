import assert from 'node:assert/strict'
import test from 'node:test'

async function loadModel() {
  return import('./marketAccounting.js').catch(() => ({}))
}

test('derives settled and pending cash without losing the sign', async () => {
  const { cashSettlement } = await loadModel()

  assert.deepEqual(cashSettlement?.(4_500, 5_000), {
    total: 4_500,
    settled: 5_000,
    pending: -500,
  })
})

test('derives economic, settled, and pending share quantities', async () => {
  const { quantitySettlement } = await loadModel()

  assert.deepEqual(quantitySettlement?.(15, 10), {
    economic: 15,
    settled: 10,
    pending: 5,
  })
})

test('formats pending T+1 and completed settlement labels', async () => {
  const { settlementLabel } = await loadModel()

  assert.equal(settlementLabel?.({ status: 'Pending', tradeDayNumber: 7, dueDayNumber: 8 }), 'Pending · T+1 · due Day 8')
  assert.equal(settlementLabel?.({ status: 'Settled', tradeDayNumber: 7, dueDayNumber: 8 }), 'Settled · Day 8')
})

test('maps every LULD state to human copy and a non-color indicator', async () => {
  const { luldPresentation } = await loadModel()

  assert.deepEqual(luldPresentation?.('Normal'), {
    label: 'Normal',
    indicator: '✓',
    tone: 'up',
    orderEntryDisabled: false,
    executionNote: 'Continuous trading is active.',
  })
  assert.equal(luldPresentation?.('LimitState').label, 'Limit State')
  assert.equal(luldPresentation?.('TradingPause').label, 'Trading Pause')
  assert.equal(luldPresentation?.('Reopening').label, 'Reopening')
  assert.equal(luldPresentation?.('Reopening').orderEntryDisabled, true)
  assert.match(luldPresentation?.('Reopening').executionNote, /reopening auction/i)
  assert.notEqual(luldPresentation?.('TradingPause').indicator, luldPresentation?.('Normal').indicator)
})
