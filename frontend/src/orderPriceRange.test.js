import assert from 'node:assert/strict'
import test from 'node:test'

async function loadModule() {
  return import('./orderPriceRange.js')
}

const company = {
  currentPrice: 100,
  lowerBandPrice: 85,
  upperBandPrice: 110,
  minimumOrderPrice: 75,
  maximumOrderPrice: 115,
}

test('reads the executable band and allowed range from a company response', async () => {
  const { orderPriceBounds } = await loadModule()
  assert.deepEqual(orderPriceBounds(company), {
    available: true,
    activeLower: 85,
    activeUpper: 110,
    allowedMin: 75,
    allowedMax: 115,
  })
})

test('reports bounds as unavailable when any field is missing', async () => {
  const { orderPriceBounds } = await loadModule()
  assert.equal(orderPriceBounds({ ...company, minimumOrderPrice: null }).available, false)
  assert.equal(orderPriceBounds({}).available, false)
  assert.equal(orderPriceBounds(null).available, false)
})

test('classifies a price against the band and allowed range at every boundary', async () => {
  const { orderPriceBounds, classifyOrderPrice } = await loadModule()
  const bounds = orderPriceBounds(company)

  assert.equal(classifyOrderPrice(85, bounds), 'executable')
  assert.equal(classifyOrderPrice(100, bounds), 'executable')
  assert.equal(classifyOrderPrice(110, bounds), 'executable')

  assert.equal(classifyOrderPrice(84.99, bounds), 'waiting')
  assert.equal(classifyOrderPrice(75, bounds), 'waiting')
  assert.equal(classifyOrderPrice(110.01, bounds), 'waiting')
  assert.equal(classifyOrderPrice(115, bounds), 'waiting')

  assert.equal(classifyOrderPrice(74.99, bounds), 'outside')
  assert.equal(classifyOrderPrice(115.01, bounds), 'outside')

  assert.equal(classifyOrderPrice(0, bounds), 'invalid')
  assert.equal(classifyOrderPrice('', bounds), 'invalid')
})

test('classifies as unavailable when the company carries no bounds', async () => {
  const { orderPriceBounds, classifyOrderPrice } = await loadModule()
  assert.equal(classifyOrderPrice(100, orderPriceBounds({})), 'unavailable')
})

test('builds bound-aware price presets from range low to range high', async () => {
  const { orderPricePresets } = await loadModule()
  assert.deepEqual(orderPricePresets(company), [
    { label: 'Range low', value: 75 },
    { label: 'Band low', value: 85 },
    { label: 'Market', value: 100 },
    { label: 'Band high', value: 110 },
    { label: 'Range high', value: 115 },
  ])
})

test('omits the market preset when no current price is known and yields nothing without bounds', async () => {
  const { orderPricePresets } = await loadModule()
  assert.deepEqual(
    orderPricePresets({ ...company, currentPrice: null }).map((preset) => preset.label),
    ['Range low', 'Band low', 'Band high', 'Range high'],
  )
  assert.deepEqual(orderPricePresets({}), [])
})
