import assert from 'node:assert/strict'
import test from 'node:test'

async function loadModule() {
  return import('./aiTraderModel.js')
}

test('formatProviderLabel prefixes the backend label', async () => {
  const { formatProviderLabel } = await loadModule()
  assert.equal(formatProviderLabel('GLM'), 'AI · GLM')
  assert.equal(formatProviderLabel('MiniMax'), 'AI · MiniMax')
  assert.equal(formatProviderLabel('Future Provider'), 'AI · Future Provider')
  assert.equal(formatProviderLabel(''), 'AI')
})

test('conversion requires a provider and a model', async () => {
  const { validateAutomation } = await loadModule()
  assert.equal(validateAutomation({ type: 'AIAgent', providerId: '', model: '' }).valid, false)
  assert.equal(validateAutomation({ type: 'AIAgent', providerId: 'glm', model: '' }).valid, false)
  assert.equal(validateAutomation({ type: 'AIAgent', providerId: 'glm', model: 'glm-4.6' }).valid, true)
})

test('individual payload omits provider and model', async () => {
  const { automationPayload } = await loadModule()
  assert.deepEqual(
    automationPayload({ type: 'Individual', providerId: 'glm', model: 'glm-4.6' }),
    { type: 'Individual' },
  )
})

test('ai payload carries provider and model but never a key', async () => {
  const { automationPayload } = await loadModule()
  assert.deepEqual(
    automationPayload({ type: 'AIAgent', providerId: 'glm', model: 'glm-4.6' }),
    { type: 'AIAgent', providerId: 'glm', model: 'glm-4.6' },
  )
})

test('test-request payload is built from provider and model', async () => {
  const { testRequestPayload } = await loadModule()
  assert.deepEqual(
    testRequestPayload({ providerId: 'glm', model: 'glm-4.6' }),
    { providerId: 'glm', model: 'glm-4.6' },
  )
})

test('max decisions per day must be a whole number of at least one', async () => {
  const { validateAutomation } = await loadModule()
  const base = { type: 'AIAgent', providerId: 'glm', model: 'glm-4.6' }
  assert.equal(validateAutomation({ ...base, maxDecisions: '3' }).valid, true)
  assert.equal(validateAutomation({ ...base, maxDecisions: '1' }).valid, true)
  assert.equal(validateAutomation({ ...base, maxDecisions: '0' }).valid, false)
  assert.equal(validateAutomation({ ...base, maxDecisions: '-2' }).valid, false)
  assert.equal(validateAutomation({ ...base, maxDecisions: '2.5' }).valid, false)
  assert.equal(validateAutomation({ ...base, maxDecisions: '' }).valid, false)
  // Omitting the field leaves validation unaffected, so the backend default applies.
  assert.equal(validateAutomation(base).valid, true)
})

test('ai payload carries max decisions per day when set and omits it otherwise', async () => {
  const { automationPayload } = await loadModule()
  const base = { type: 'AIAgent', providerId: 'glm', model: 'glm-4.6' }
  assert.deepEqual(automationPayload({ ...base, maxDecisions: '5' }), {
    type: 'AIAgent',
    providerId: 'glm',
    model: 'glm-4.6',
    maxDecisionsPerDay: 5,
  })
  assert.deepEqual(automationPayload({ ...base, maxDecisions: '' }), {
    type: 'AIAgent',
    providerId: 'glm',
    model: 'glm-4.6',
  })
})

test('formatStoredJson pretty-prints json and never evaluates content', async () => {
  const { formatStoredJson } = await loadModule()
  assert.equal(formatStoredJson('{"a":1}'), '{\n  "a": 1\n}')
  assert.equal(formatStoredJson('<script>alert(1)</script>'), '<script>alert(1)</script>')
  assert.equal(formatStoredJson(''), '')
})

test('AI call presentation extracts multiline thinking from the provider content', async () => {
  const { parseAiCallPresentation } = await loadModule()
  const responseBody = JSON.stringify({
    choices: [{
      message: {
        content: '<think>\nFirst observation.\n\nSecond observation.\n</think>\n{"summary":"Done","orders":[]}',
      },
    }],
  })

  assert.equal(
    parseAiCallPresentation(responseBody, null).thinking,
    'First observation.\n\nSecond observation.',
  )
})

test('AI call presentation reads a dedicated reasoning field', async () => {
  const { parseAiCallPresentation } = await loadModule()
  const responseBody = JSON.stringify({
    choices: [{ message: { reasoning_content: 'Compare value.\nCheck risk.', content: '{"summary":"Done"}' } }],
  })

  assert.equal(parseAiCallPresentation(responseBody, null).thinking, 'Compare value.\nCheck risk.')
})

test('AI call presentation preserves summary line breaks', async () => {
  const { parseAiCallPresentation } = await loadModule()
  const decisionJson = JSON.stringify({ summary: 'Portfolio reviewed.\nFive orders selected.', orders: [] })

  assert.equal(
    parseAiCallPresentation(null, decisionJson).summary,
    'Portfolio reviewed.\nFive orders selected.',
  )
})

test('AI call presentation exposes decision rows', async () => {
  const { parseAiCallPresentation } = await loadModule()
  const decisionJson = JSON.stringify({
    summary: 'Buy selectively.',
    orders: [{
      side: 'Buy',
      companyId: 117,
      quantity: 20000,
      priceOffsetPercent: 1.75,
      reason: 'Strong sector momentum.',
    }],
  })

  assert.deepEqual(parseAiCallPresentation(null, decisionJson).orders, [{
    side: 'Buy',
    companyId: 117,
    quantity: 20000,
    priceOffsetPercent: 1.75,
    reason: 'Strong sector momentum.',
  }])
})

test('AI call presentation exposes a Big Investment decision', async () => {
  const { parseAiCallPresentation } = await loadModule()
  const decisionJson = JSON.stringify({
    summary: 'Fund a company directly.',
    bigInvestment: {
      companyId: 117,
      amount: 50000,
      reason: 'Durable growth opportunity.',
    },
    orders: [],
  })

  assert.deepEqual(parseAiCallPresentation(null, decisionJson).bigInvestment, {
    companyId: 117,
    amount: 50000,
    reason: 'Durable growth opportunity.',
  })
})

test('AI call presentation exposes a Big Investment application outcome', async () => {
  const { parseAiCallPresentation } = await loadModule()
  const applicationResultJson = JSON.stringify({
    bigInvestment: {
      companyId: 117,
      amount: 50000,
      reason: 'Durable growth opportunity.',
      applied: false,
      sharesMinted: 0,
      rejectionReason: 'The opportunity is no longer available.',
    },
  })

  assert.deepEqual(
    parseAiCallPresentation(null, null, applicationResultJson).bigInvestmentApplication,
    {
      companyId: 117,
      amount: 50000,
      reason: 'Durable growth opportunity.',
      applied: false,
      sharesMinted: 0,
      rejectionReason: 'The opportunity is no longer available.',
    },
  )
})

test('AI call presentation safely handles malformed stored payloads', async () => {
  const { parseAiCallPresentation } = await loadModule()

  assert.deepEqual(parseAiCallPresentation('not json', '{broken'), {
    thinking: '',
    summary: '',
    orders: [],
    bigInvestment: null,
    bigInvestmentApplication: null,
  })
})

test('decision-quality summary formats rates and flags activity', async () => {
  const { formatDecisionQuality } = await loadModule()
  const view = formatDecisionQuality({
    callAttempts: 4,
    completedCalls: 2,
    invalidJsonCalls: 1,
    otherFailedCalls: 1,
    callCompletionRate: 0.5,
    proposedOrders: 8,
    appliedOrders: 5,
    rejectedOrders: 3,
    proposalAcceptanceRate: 0.625,
    executedBuyNotional: 150,
  })

  assert.equal(view.hasActivity, true)
  assert.equal(view.callCompletion, '50%')
  assert.equal(view.completedCalls, 2)
  assert.equal(view.callAttempts, 4)
  assert.equal(view.invalidJsonCalls, 1)
  assert.equal(view.proposalAcceptance, '63%')
  assert.equal(view.appliedOrders, 5)
  assert.equal(view.proposedOrders, 8)
  assert.equal(view.executedBuyNotional, 150)
})

test('decision-quality summary is inactive and safe with no data', async () => {
  const { formatDecisionQuality } = await loadModule()

  assert.equal(formatDecisionQuality(null), null)
  const empty = formatDecisionQuality({})
  assert.equal(empty.hasActivity, false)
  assert.equal(empty.callCompletion, '0%')
  assert.equal(empty.proposalAcceptance, '0%')
  assert.equal(empty.executedBuyNotional, 0)
})
