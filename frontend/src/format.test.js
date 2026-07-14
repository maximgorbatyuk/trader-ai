import assert from 'node:assert/strict'
import test from 'node:test'

import {
  RATING_LABEL,
  RATING_TAG_CLASS,
  ratingImpactLabel,
  ratingTrend,
} from './format.js'

test('presents raised expectations as a positive rating', () => {
  assert.equal(RATING_LABEL.RaisedExpectations, 'Raised expectations')
  assert.equal(RATING_TAG_CLASS.RaisedExpectations, 'tag-rating-raised')
  assert.equal(ratingImpactLabel('RaisedExpectations', 12.4), ' +12%')
})

test('presents extra raised expectations as the strongest positive rating', () => {
  assert.equal(RATING_LABEL.ExtraRaisedExpectations, 'Extra raised expectations')
  assert.equal(RATING_TAG_CLASS.ExtraRaisedExpectations, 'tag-rating-raised')
  assert.equal(ratingImpactLabel('ExtraRaisedExpectations', 17.6), ' +18%')
  assert.equal(ratingTrend('ExtraRaisedExpectations', 'RaisedExpectations'), 'improved')
  assert.equal(ratingTrend('RaisedExpectations', 'ExtraRaisedExpectations'), 'worsened')
})

test('orders raised expectations below low risk for rating trends', () => {
  assert.equal(ratingTrend('RaisedExpectations', 'Low'), 'improved')
  assert.equal(ratingTrend('Low', 'RaisedExpectations'), 'worsened')
  assert.equal(ratingTrend('Extra', 'High'), 'worsened')
  assert.equal(ratingTrend('Low', 'Low'), null)
})

test('keeps extra-risk impact negative and hides missing impacts', () => {
  assert.equal(ratingImpactLabel('Extra', 17.6), ' −18%')
  assert.equal(ratingImpactLabel('Low', 10), '')
  assert.equal(ratingImpactLabel('RaisedExpectations'), '')
})
