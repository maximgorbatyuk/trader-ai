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

test('first conversion requires provider, model, and key', async () => {
  const { validateAutomation } = await loadModule()
  assert.equal(
    validateAutomation({ type: 'AIAgent', providerId: '', model: '', apiKey: '', originalProviderId: null }).valid,
    false,
  )
  assert.equal(
    validateAutomation({ type: 'AIAgent', providerId: 'glm', model: 'glm-4.6', apiKey: '', originalProviderId: null }).valid,
    false,
  )
  assert.equal(
    validateAutomation({ type: 'AIAgent', providerId: 'glm', model: 'glm-4.6', apiKey: 'k', originalProviderId: null }).valid,
    true,
  )
})

test('an empty key is allowed when the provider is unchanged', async () => {
  const { validateAutomation } = await loadModule()
  assert.equal(
    validateAutomation({ type: 'AIAgent', providerId: 'glm', model: 'glm-4.5', apiKey: '', originalProviderId: 'glm' }).valid,
    true,
  )
})

test('a new key is required when the provider changes', async () => {
  const { validateAutomation } = await loadModule()
  assert.equal(
    validateAutomation({ type: 'AIAgent', providerId: 'minimax', model: 'MiniMax-M2', apiKey: '', originalProviderId: 'glm' }).valid,
    false,
  )
})

test('individual payload omits provider, model, and key', async () => {
  const { automationPayload } = await loadModule()
  assert.deepEqual(
    automationPayload({ type: 'Individual', providerId: 'glm', model: 'glm-4.6', apiKey: 'x', originalProviderId: 'glm' }),
    { type: 'Individual' },
  )
})

test('ai payload carries provider, model, and a supplied key', async () => {
  const { automationPayload } = await loadModule()
  assert.deepEqual(
    automationPayload({ type: 'AIAgent', providerId: 'glm', model: 'glm-4.6', apiKey: 'secret', originalProviderId: null }),
    { type: 'AIAgent', providerId: 'glm', model: 'glm-4.6', apiKey: 'secret' },
  )
})

test('ai payload omits the key when blank so the stored key is retained', async () => {
  const { automationPayload } = await loadModule()
  assert.deepEqual(
    automationPayload({ type: 'AIAgent', providerId: 'glm', model: 'glm-4.5', apiKey: '', originalProviderId: 'glm' }),
    { type: 'AIAgent', providerId: 'glm', model: 'glm-4.5' },
  )
})

test('test-request payload is built from provider, model, and key', async () => {
  const { testRequestPayload } = await loadModule()
  assert.deepEqual(
    testRequestPayload({ providerId: 'glm', model: 'glm-4.6', apiKey: 'k' }),
    { providerId: 'glm', model: 'glm-4.6', apiKey: 'k' },
  )
  assert.deepEqual(
    testRequestPayload({ providerId: 'glm', model: 'glm-4.6', apiKey: '' }),
    { providerId: 'glm', model: 'glm-4.6' },
  )
})

test('max decisions per day must be a whole number of at least one', async () => {
  const { validateAutomation } = await loadModule()
  const base = { type: 'AIAgent', providerId: 'glm', model: 'glm-4.6', apiKey: 'k', originalProviderId: null }
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
  const base = { type: 'AIAgent', providerId: 'glm', model: 'glm-4.6', apiKey: 'secret', originalProviderId: null }
  assert.deepEqual(automationPayload({ ...base, maxDecisions: '5' }), {
    type: 'AIAgent',
    providerId: 'glm',
    model: 'glm-4.6',
    apiKey: 'secret',
    maxDecisionsPerDay: 5,
  })
  assert.deepEqual(automationPayload({ ...base, maxDecisions: '' }), {
    type: 'AIAgent',
    providerId: 'glm',
    model: 'glm-4.6',
    apiKey: 'secret',
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
      limitPrice: 308.86,
      reason: 'Strong sector momentum.',
    }],
  })

  assert.deepEqual(parseAiCallPresentation(null, decisionJson).orders, [{
    side: 'Buy',
    companyId: 117,
    quantity: 20000,
    limitPrice: 308.86,
    reason: 'Strong sector momentum.',
  }])
})

test('AI call presentation safely handles malformed stored payloads', async () => {
  const { parseAiCallPresentation } = await loadModule()

  assert.deepEqual(parseAiCallPresentation('not json', '{broken'), {
    thinking: '',
    summary: '',
    orders: [],
  })
})
