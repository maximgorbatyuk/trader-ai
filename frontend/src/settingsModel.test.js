import assert from 'node:assert/strict'
import test from 'node:test'
import { buildSettingsUpdate, groupSettings, toDraftValue } from './settingsModel.js'

test('groups settings by section and subsection without losing order', () => {
  const groups = groupSettings([
    { key: 'Margin:Enabled', section: 'Margin', subsection: null },
    { key: 'AiTrading:Providers:glm:Models', section: 'AI trading', subsection: 'Providers' },
    { key: 'AiTrading:MaxOrdersPerDecision', section: 'AI trading', subsection: null },
  ])

  assert.deepEqual(
    groups.map((group) => ({
      section: group.section,
      subsections: group.subsections.map((subsection) => subsection.name),
    })),
    [
      { section: 'Margin', subsections: [null] },
      { section: 'AI trading', subsections: [null, 'Providers'] },
    ],
  )
})

test('formats string lists as one editable value per line', () => {
  assert.equal(
    toDraftValue({ valueType: 'StringList', value: ['glm-4.6', 'glm-4.5'] }),
    'glm-4.6\nglm-4.5',
  )
})

test('serializes dirty drafts using each setting value type', () => {
  const settings = [
    { key: 'Margin:Enabled', valueType: 'Boolean' },
    { key: 'MarketLoop:IntervalSeconds', valueType: 'Integer' },
    { key: 'Margin:InitialMarginRate', valueType: 'Decimal' },
    { key: 'AiTrading:Providers:glm:Models', valueType: 'StringList' },
  ]
  const drafts = {
    'Margin:Enabled': false,
    'MarketLoop:IntervalSeconds': '3',
    'Margin:InitialMarginRate': '0.40',
    'AiTrading:Providers:glm:Models': 'glm-4.6\n glm-4.5 ',
  }

  assert.deepEqual(buildSettingsUpdate(settings, drafts, new Set(settings.map((setting) => setting.key))), {
    values: {
      'Margin:Enabled': false,
      'MarketLoop:IntervalSeconds': 3,
      'Margin:InitialMarginRate': 0.4,
      'AiTrading:Providers:glm:Models': ['glm-4.6', 'glm-4.5'],
    },
  })
})

test('secret drafts are submitted only when a replacement is typed', () => {
  const settings = [
    { key: 'AiTrading:Providers:glm:ApiKey', valueType: 'Secret' },
    { key: 'AiTrading:Providers:minimax:ApiKey', valueType: 'Secret' },
  ]
  const drafts = {
    'AiTrading:Providers:glm:ApiKey': ' new-key ',
    'AiTrading:Providers:minimax:ApiKey': '   ',
  }

  assert.deepEqual(buildSettingsUpdate(settings, drafts, new Set(settings.map((setting) => setting.key))), {
    values: { 'AiTrading:Providers:glm:ApiKey': 'new-key' },
  })
})
