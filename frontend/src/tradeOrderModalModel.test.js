import assert from 'node:assert/strict'
import test from 'node:test'

async function loadModel() {
  return import('./tradeOrderModalModel.js').catch(() => ({}))
}

const executableCompany = {
  luldState: 'Normal',
  lowerBandPrice: 90,
  upperBandPrice: 110,
  minimumOrderPrice: 80,
  maximumOrderPrice: 120,
}

const availableOrder = {
  actorId: 1,
  orderParticipantId: 2,
  remaining: 10,
  price: 100,
  company: executableCompany,
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

test('rejects a buy above margin buying power before submission', async () => {
  const { tradeOrderEligibility } = await loadModel()

  assert.deepEqual(
    tradeOrderEligibility?.({
      ...availableOrder,
      remaining: 30,
      side: 'Buy',
      quantity: 21,
      ownedShares: 0,
      buyingPower: 2_000,
    }),
    { eligible: false, reason: 'Insufficient margin buying power.' },
  )
})

test('rejects opening an order without a selected actor', async () => {
  const { tradeOrderAvailability } = await loadModel()

  assert.deepEqual(tradeOrderAvailability?.({ ...availableOrder, actorId: null }), {
    eligible: false,
    reason: 'Select a player or managed fund to accept this order.',
  })
})

test("rejects accepting the selected actor's own order", async () => {
  const { tradeOrderAvailability } = await loadModel()

  assert.deepEqual(tradeOrderAvailability?.({ ...availableOrder, orderParticipantId: 1 }), {
    eligible: false,
    reason: 'You cannot accept your own order.',
  })
})

test('rejects an order with no remaining shares', async () => {
  const { tradeOrderAvailability } = await loadModel()

  assert.deepEqual(tradeOrderAvailability?.({ ...availableOrder, remaining: 0 }), {
    eligible: false,
    reason: 'This order has no remaining shares.',
  })
})

test('rejects a quantity above the remaining order size', async () => {
  const { tradeOrderEligibility } = await loadModel()

  assert.deepEqual(
    tradeOrderEligibility?.({
      ...availableOrder,
      side: 'Buy',
      quantity: 11,
      ownedShares: 0,
      buyingPower: 2_000,
    }),
    { eligible: false, reason: 'Quantity exceeds the remaining order size.' },
  )
})

test('rejects orders outside the executable price band', async () => {
  const { tradeOrderAvailability } = await loadModel()

  assert.deepEqual(tradeOrderAvailability?.({ ...availableOrder, price: 85 }), {
    eligible: false,
    reason: 'This order is waiting outside the executable price band.',
  })
  assert.deepEqual(tradeOrderAvailability?.({ ...availableOrder, price: 125 }), {
    eligible: false,
    reason: 'This order is outside the allowed price range.',
  })
})

test('rejects an invalid order price when price bounds are unavailable', async () => {
  const { tradeOrderAvailability } = await loadModel()

  assert.deepEqual(tradeOrderAvailability?.({ ...availableOrder, price: 0, company: null }), {
    eligible: false,
    reason: 'This order has an invalid price.',
  })
})

test('rejects new orders during every non-normal LULD state', async () => {
  const { tradeOrderEligibility } = await loadModel()

  for (const state of ['LimitState', 'TradingPause', 'Reopening']) {
    assert.deepEqual(
      tradeOrderEligibility?.({
        ...availableOrder,
        side: 'Buy',
        quantity: 1,
        ownedShares: 0,
        buyingPower: 1_000,
        company: { ...executableCompany, luldState: state },
      }),
      {
        eligible: false,
        reason: `Order entry is disabled during ${{ LimitState: 'Limit State', TradingPause: 'Trading Pause', Reopening: 'Reopening' }[state]}.`,
      },
    )
  }
})
