import assert from 'node:assert/strict'
import test from 'node:test'
import { createServer } from 'vite'

async function loadModule(t) {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())
  return server.ssrLoadModule('/src/appShellModel.js')
}

test('seeds the demo market when the first shell load finds no market', async (t) => {
  const { loadShellSnapshot } = await loadModule(t)
  assert.equal(typeof loadShellSnapshot, 'function')

  let seedCalls = 0
  const seededMarket = { id: 1, status: 'NotStarted' }
  const snapshot = await loadShellSnapshot({
    getMarket: async () => null,
    getPlayer: async () => null,
    seedMarket: async () => {
      seedCalls += 1
      return seededMarket
    },
  })

  assert.equal(seedCalls, 1)
  assert.deepEqual(snapshot, { market: seededMarket, player: null })
})

test('keeps an existing market without reseeding it', async (t) => {
  const { loadShellSnapshot } = await loadModule(t)
  assert.equal(typeof loadShellSnapshot, 'function')

  let seedCalls = 0
  const market = { id: 7, status: 'Paused' }
  const player = { id: 9, name: 'Ada' }
  const snapshot = await loadShellSnapshot({
    getMarket: async () => market,
    getPlayer: async () => player,
    seedMarket: async () => {
      seedCalls += 1
      return { id: 8 }
    },
  })

  assert.equal(seedCalls, 0)
  assert.deepEqual(snapshot, { market, player })
})
