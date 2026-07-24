import assert from 'node:assert/strict'
import test from 'node:test'

import {
  RATING_LABEL,
  RATING_TAG_CLASS,
  ratingImpactLabel,
  ratingTrend,
} from './format.js'

test('defines exactly the five final audit statuses with readable labels and tones', () => {
  assert.deepEqual(Object.keys(RATING_LABEL), [
    'ExtraRaisedExpectations',
    'RaisedExpectations',
    'Stable',
    'LowRisk',
    'HighRisk',
  ])
  assert.deepEqual(RATING_LABEL, {
    ExtraRaisedExpectations: 'Extra raised expectations',
    RaisedExpectations: 'Raised expectations',
    Stable: 'Stable',
    LowRisk: 'Low risk',
    HighRisk: 'High risk',
  })
  assert.deepEqual(RATING_TAG_CLASS, {
    ExtraRaisedExpectations: 'tag-rating-extra-raised',
    RaisedExpectations: 'tag-rating-raised',
    Stable: 'tag-rating-stable',
    LowRisk: 'tag-rating-low',
    HighRisk: 'tag-rating-high',
  })
})

test('orders every audit status from strongest outlook to highest risk', () => {
  const ordered = [
    'ExtraRaisedExpectations',
    'RaisedExpectations',
    'Stable',
    'LowRisk',
    'HighRisk',
  ]
  for (let index = 0; index < ordered.length - 1; index += 1) {
    assert.equal(ratingTrend(ordered[index], ordered[index + 1]), 'improved')
    assert.equal(ratingTrend(ordered[index + 1], ordered[index]), 'worsened')
  }
  assert.equal(ratingTrend('Stable', 'Stable'), null)
  assert.equal(ratingTrend('UnknownRating', 'HighRisk'), null)
})

test('signs directional audit impacts and hides neutral or missing impacts', () => {
  assert.equal(ratingImpactLabel('ExtraRaisedExpectations', 17.6), ' +18%')
  assert.equal(ratingImpactLabel('RaisedExpectations', 12.4), ' +12%')
  assert.equal(ratingImpactLabel('HighRisk', 17.6), ' −18%')
  assert.equal(ratingImpactLabel('Stable', 10), '')
  assert.equal(ratingImpactLabel('LowRisk', 10), '')
  assert.equal(ratingImpactLabel('RaisedExpectations'), '')
})
