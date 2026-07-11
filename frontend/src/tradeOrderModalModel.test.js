import assert from 'node:assert/strict'
import test from 'node:test'

async function loadModel() {
  return import('./tradeOrderModalModel.js').catch(() => ({}))
}

test('offers the approved quantity percentages', async () => {
  const { TRADE_QUANTITY_PRESETS } = await loadModel()

  assert.deepEqual(TRADE_QUANTITY_PRESETS, [
    { label: '1%', value: 0.01 },
    { label: '5%', value: 0.05 },
    { label: '10%', value: 0.1 },
    { label: '25%', value: 0.25 },
    { label: '50%', value: 0.5 },
    { label: '75%', value: 0.75 },
    { label: '100%', value: 1 },
  ])
})

test('maps the latest 48 company price snapshots', async () => {
  const { recentPriceValues } = await loadModel()
  const snapshots = Array.from({ length: 52 }, (_, index) => ({ price: index + 1 }))

  assert.deepEqual(recentPriceValues?.(snapshots), Array.from({ length: 48 }, (_, index) => index + 5))
})

test('maps the latest 48 industry sentiment snapshots', async () => {
  const { recentSentimentValues } = await loadModel()
  const snapshots = Array.from({ length: 50 }, (_, index) => ({ sentimentValue: index - 25 }))

  assert.deepEqual(recentSentimentValues?.(snapshots), Array.from({ length: 48 }, (_, index) => index - 23))
})
