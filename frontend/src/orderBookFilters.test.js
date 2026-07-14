import assert from 'node:assert/strict'
import test from 'node:test'

async function loadModel() {
  return import('./orderBookFilters.js').catch(() => ({}))
}

const orders = [
  { id: 1, companyId: 10 },
  { id: 2, companyId: 20 },
  { id: 3, companyId: 30 },
]
const companyById = new Map([
  [10, { id: 10, isFavorite: true }],
  [20, { id: 20, isFavorite: false }],
])

test('offers Favorite, Owned, and All while keeping Owned as the default', async () => {
  const { BUY_FILTER_OPTIONS, DEFAULT_BUY_FILTER } = await loadModel()

  assert.deepEqual(BUY_FILTER_OPTIONS, [
    { value: 'favorite', label: 'Favorite' },
    { value: 'owned', label: 'Owned' },
    { value: 'all', label: 'All' },
  ])
  assert.equal(DEFAULT_BUY_FILTER, 'owned')
})

test('filters buy orders to favorite companies', async () => {
  const { filterBuyOrders } = await loadModel()

  assert.deepEqual(filterBuyOrders?.(orders, 'favorite', new Set(), companyById), [orders[0]])
})

test('preserves the existing owned-company filter', async () => {
  const { filterBuyOrders } = await loadModel()

  assert.deepEqual(filterBuyOrders?.(orders, 'owned', new Set([20]), companyById), [orders[1]])
})

test('keeps every buy order for the All filter', async () => {
  const { filterBuyOrders } = await loadModel()

  assert.deepEqual(filterBuyOrders?.(orders, 'all', new Set(), companyById), orders)
})
