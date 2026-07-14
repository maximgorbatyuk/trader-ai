import assert from 'node:assert/strict'
import test from 'node:test'

async function loadModel() {
  return import('./orderBookSort.js').catch(() => ({}))
}

const rows = [
  {
    order: { id: 30 },
    sortValues: {
      orderPrice: 100,
      marketPrice: 110,
      quantity: 10,
      ownedShares: 8,
      gainLoss: 20,
      company: 'Zebra',
      trader: 'Beta',
    },
  },
  {
    order: { id: 10 },
    sortValues: {
      orderPrice: 90,
      marketPrice: null,
      quantity: 20,
      ownedShares: 0,
      gainLoss: null,
      company: 'Alpha',
      trader: 'Charlie',
    },
  },
  {
    order: { id: 20 },
    sortValues: {
      orderPrice: 110,
      marketPrice: 100,
      quantity: 5,
      ownedShares: 12,
      gainLoss: -5,
      company: 'Gamma',
      trader: 'Alpha',
    },
  },
]

function ids(sortedRows) {
  return sortedRows?.map((row) => row.order.id)
}

test('defaults the buy book to best gain or loss and preserves the sell price default', async () => {
  const { ORDER_BOOK_DEFAULT_SORT } = await loadModel()

  assert.deepEqual(ORDER_BOOK_DEFAULT_SORT, {
    Buy: { key: 'gainLoss', direction: 'desc' },
    Sell: { key: 'orderPrice', direction: 'desc' },
  })
})

test('sorts the most beneficial gain or loss first and leaves unavailable values last', async () => {
  const { sortOrderBookRows } = await loadModel()

  assert.deepEqual(ids(sortOrderBookRows?.(rows, 'gainLoss', 'desc')), [30, 20, 10])
  assert.deepEqual(ids(sortOrderBookRows?.(rows, 'gainLoss', 'asc')), [20, 30, 10])
})

test('distinguishes no selected actor from a selected actor with no owned shares', async () => {
  const { orderBookOwnedShares } = await loadModel()

  assert.equal(orderBookOwnedShares?.(null, { shares: 8 }), null)
  assert.equal(orderBookOwnedShares?.({ id: 1 }, undefined), 0)
  assert.equal(orderBookOwnedShares?.({ id: 1 }, { shares: 8 }), 8)
})

test('sorts every visible order-book column', async () => {
  const { sortOrderBookRows } = await loadModel()

  assert.deepEqual(ids(sortOrderBookRows?.(rows, 'orderPrice', 'desc')), [20, 30, 10])
  assert.deepEqual(ids(sortOrderBookRows?.(rows, 'marketPrice', 'desc')), [30, 20, 10])
  assert.deepEqual(ids(sortOrderBookRows?.(rows, 'quantity', 'desc')), [10, 30, 20])
  assert.deepEqual(ids(sortOrderBookRows?.(rows, 'ownedShares', 'desc')), [20, 30, 10])
  assert.deepEqual(ids(sortOrderBookRows?.(rows, 'company', 'desc')), [30, 20, 10])
  assert.deepEqual(ids(sortOrderBookRows?.(rows, 'trader', 'desc')), [10, 30, 20])
})

test('uses the order id to keep equal values deterministic across live refreshes', async () => {
  const { sortOrderBookRows } = await loadModel()
  const tiedRows = [
    { order: { id: 2 }, sortValues: { orderPrice: 100 } },
    { order: { id: 1 }, sortValues: { orderPrice: 100 } },
  ]

  assert.deepEqual(ids(sortOrderBookRows?.(tiedRows, 'orderPrice', 'desc')), [1, 2])
})
