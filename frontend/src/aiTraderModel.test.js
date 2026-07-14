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

test('formatStoredJson pretty-prints json and never evaluates content', async () => {
  const { formatStoredJson } = await loadModule()
  assert.equal(formatStoredJson('{"a":1}'), '{\n  "a": 1\n}')
  assert.equal(formatStoredJson('<script>alert(1)</script>'), '<script>alert(1)</script>')
  assert.equal(formatStoredJson(''), '')
})
