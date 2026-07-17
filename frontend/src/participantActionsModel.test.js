import assert from 'node:assert/strict'
import test from 'node:test'

import { parseCashAdjustment, transferableSettledCash } from './participantActionsModel.js'

test('cash adjustment accepts positive and negative non-zero amounts', () => {
  assert.equal(parseCashAdjustment('1250.50'), 1250.5)
  assert.equal(parseCashAdjustment('-400'), -400)
})

test('cash adjustment rejects blank, zero, and non-finite values', () => {
  assert.equal(parseCashAdjustment(''), null)
  assert.equal(parseCashAdjustment('0'), null)
  assert.equal(parseCashAdjustment('not a number'), null)
  assert.equal(parseCashAdjustment('Infinity'), null)
})

test('transferable settled cash respects reservations and settlement', () => {
  assert.equal(transferableSettledCash({ availableBalance: 800, settledCashBalance: 600 }), 600)
  assert.equal(transferableSettledCash({ availableBalance: 400, settledCashBalance: 900 }), 400)
  assert.equal(transferableSettledCash({ availableBalance: -10, settledCashBalance: 900 }), 0)
})
