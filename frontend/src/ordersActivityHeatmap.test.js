import assert from 'node:assert/strict'
import test from 'node:test'

async function loadHeatmap() {
  return import('./ordersActivityHeatmap.js').catch(() => ({}))
}

test('groups the latest three trading days into six rows and twenty-one columns', async () => {
  const { buildOrdersActivityHeatmap } = await loadHeatmap()
  const activity = [
    { tradingDayNumber: 1, tradingCycleNumber: 1, ordersPlaced: 99, paidDividend: false },
    { tradingDayNumber: 2, tradingCycleNumber: 1, ordersPlaced: 1, paidDividend: false },
    { tradingDayNumber: 2, tradingCycleNumber: 5, ordersPlaced: 2, paidDividend: true },
    { tradingDayNumber: 2, tradingCycleNumber: 6, ordersPlaced: 4, paidDividend: false },
    { tradingDayNumber: 3, tradingCycleNumber: 210, ordersPlaced: 7, paidDividend: false },
    { tradingDayNumber: 4, tradingCycleNumber: 1, ordersPlaced: 0, paidDividend: false },
  ]

  const heatmap = buildOrdersActivityHeatmap?.(activity, 210)

  assert.deepEqual(heatmap?.days, [2, 3, 4])
  assert.equal(heatmap?.rows.length, 6)
  assert.ok(heatmap?.rows.every((row) => row.length === 21))
  assert.deepEqual(heatmap?.rowRanges, [
    { start: 1, end: 35 },
    { start: 36, end: 70 },
    { start: 71, end: 105 },
    { start: 106, end: 140 },
    { start: 141, end: 175 },
    { start: 176, end: 210 },
  ])

  assert.deepEqual(heatmap?.rows[0][0], {
    dayNumber: 2,
    cycleStart: 1,
    cycleEnd: 5,
    ordersPlaced: 3,
    hasData: true,
    paidDividend: true,
    level: 3,
  })
  assert.equal(heatmap?.rows[0][1].ordersPlaced, 4)
  assert.equal(heatmap?.rows[5][13].ordersPlaced, 7)
  assert.equal(heatmap?.rows[0][14].hasData, true)
  assert.equal(heatmap?.rows[0][15].hasData, false)
  assert.equal(heatmap?.peakOrders, 7)
})

test('derives cell boundaries from the supplied trading-day length', async () => {
  const { buildOrdersActivityHeatmap } = await loadHeatmap()
  const activity = [
    { tradingDayNumber: 8, tradingCycleNumber: 2, ordersPlaced: 2, paidDividend: false },
    { tradingDayNumber: 8, tradingCycleNumber: 3, ordersPlaced: 3, paidDividend: false },
  ]

  const heatmap = buildOrdersActivityHeatmap?.(activity, 84)

  assert.deepEqual(heatmap?.days, [null, null, 8])
  assert.deepEqual(heatmap?.rowRanges[0], { start: 1, end: 14 })
  assert.deepEqual(heatmap?.rows[0][14], {
    dayNumber: 8,
    cycleStart: 1,
    cycleEnd: 2,
    ordersPlaced: 2,
    hasData: true,
    paidDividend: false,
    level: 4,
  })
  assert.equal(heatmap?.rows[0][15].cycleStart, 3)
  assert.equal(heatmap?.rows[0][15].cycleEnd, 4)
  assert.equal(heatmap?.rows[0][15].ordersPlaced, 3)
  assert.equal(heatmap?.rows[0][15].level, 5)
})
