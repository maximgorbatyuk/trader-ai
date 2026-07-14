import assert from 'node:assert/strict'
import test from 'node:test'

async function loadModel() {
  return import('./favoriteCompanies.js').catch(() => ({}))
}

test('selects favorite companies without changing market order', async () => {
  const { favoriteCompanies } = await loadModel()
  const companies = [
    { id: 1, isFavorite: true },
    { id: 2, isFavorite: false },
    { id: 3, isFavorite: true },
  ]

  assert.deepEqual(favoriteCompanies?.(companies), [companies[0], companies[2]])
})

test('matches the dedicated market-map favorite filters', async () => {
  const { matchesFavoriteFilter } = await loadModel()
  const favorite = { isFavorite: true }
  const ordinary = { isFavorite: false }

  assert.equal(matchesFavoriteFilter?.(favorite, 'all'), true)
  assert.equal(matchesFavoriteFilter?.(favorite, 'favorite'), true)
  assert.equal(matchesFavoriteFilter?.(ordinary, 'favorite'), false)
  assert.equal(matchesFavoriteFilter?.(favorite, 'not-favorite'), false)
  assert.equal(matchesFavoriteFilter?.(ordinary, 'not-favorite'), true)
})
