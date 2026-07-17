import assert from 'node:assert/strict'
import test from 'node:test'

async function loadModel() {
  return import('./favoriteTraders.js').catch(() => ({}))
}

test('selects favorite traders without changing roster order', async () => {
  const { favoriteTraders } = await loadModel()
  const participants = [
    { id: 1, isFavorite: true },
    { id: 2, isFavorite: false },
    { id: 3, isFavorite: true },
  ]

  assert.deepEqual(favoriteTraders?.(participants), [participants[0], participants[2]])
})
