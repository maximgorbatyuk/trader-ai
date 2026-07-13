import assert from 'node:assert/strict'
import test from 'node:test'

async function loadPresentation() {
  return import('./cashMovements.js').catch(() => ({}))
}

test('maps corporate cash movements to visible and accessible cash directions', async () => {
  const { corporateCashMovementPresentation } = await loadPresentation()

  assert.deepEqual(corporateCashMovementPresentation?.('OperatingIncome'), {
    label: 'Operating income',
    direction: 'Credit',
    sign: '+',
    tone: 'up',
  })
  assert.deepEqual(corporateCashMovementPresentation?.('PrimaryIssuance'), {
    label: 'Primary issuance',
    direction: 'Credit',
    sign: '+',
    tone: 'up',
  })
  assert.deepEqual(corporateCashMovementPresentation?.('DividendDeclared'), {
    label: 'Dividend paid',
    direction: 'Debit',
    sign: '−',
    tone: 'down',
  })
  assert.deepEqual(corporateCashMovementPresentation?.('ClosureDistribution'), {
    label: 'Closure distribution',
    direction: 'Debit',
    sign: '−',
    tone: 'down',
  })
})

test('renders an unknown corporate cash movement without inventing a cash direction', async () => {
  const { corporateCashMovementPresentation } = await loadPresentation()

  assert.deepEqual(corporateCashMovementPresentation?.('UnexpectedAdjustment'), {
    label: 'Unexpected adjustment',
    direction: 'Movement',
    sign: '',
    tone: 'neutral',
  })
})
