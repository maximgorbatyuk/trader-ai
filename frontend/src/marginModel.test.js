import assert from 'node:assert/strict'
import test from 'node:test'

async function loadModule() {
  return import('./marginModel.js')
}

test('formats separate cash and margin affordability', async () => {
  const { affordability } = await loadModule()
  assert.equal(typeof affordability, 'function')
  assert.deepEqual(affordability(1_000, 2_000, 100), { cashShares: 10, marginShares: 20 })
})

test('normalizes signed maintenance excess into excess and deficiency', async () => {
  const { maintenanceStanding } = await loadModule()
  assert.equal(typeof maintenanceStanding, 'function')
  assert.deepEqual(maintenanceStanding(-125), { excess: 0, deficiency: 125 })
  assert.deepEqual(maintenanceStanding(80), { excess: 80, deficiency: 0 })
})
