// Pure helpers for the AI-trader automation UI. These hold the client-side rules for provider/model/key entry
// and formatting; the backend remains authoritative for validation. Nothing here evaluates or renders HTML.

function isNonBlank(value) {
  return typeof value === 'string' && value.trim().length > 0
}

// A new key is required for a first conversion or whenever the provider changes; changing only the model on the
// same provider reuses the stored key.
export function requiresNewApiKey(state) {
  return !state.originalProviderId || state.providerId !== state.originalProviderId
}

export function validateAutomation(state) {
  if (state.type === 'Individual') {
    return { valid: true }
  }

  if (state.type !== 'AIAgent') {
    return { valid: false, error: 'Unsupported automation type.' }
  }

  if (!isNonBlank(state.providerId)) {
    return { valid: false, error: 'Select a provider.' }
  }

  if (!isNonBlank(state.model)) {
    return { valid: false, error: 'Enter a model name.' }
  }

  if (requiresNewApiKey(state) && !isNonBlank(state.apiKey)) {
    return { valid: false, error: 'Enter an API key.' }
  }

  if (state.maxDecisions !== undefined && state.maxDecisions !== null) {
    const maxDecisions = Number(state.maxDecisions)
    if (!Number.isInteger(maxDecisions) || maxDecisions < 1) {
      return { valid: false, error: 'Max decisions per day must be a whole number of at least 1.' }
    }
  }

  return { valid: true }
}

export function automationPayload(state) {
  if (state.type === 'Individual') {
    return { type: 'Individual' }
  }

  const payload = { type: 'AIAgent', providerId: state.providerId, model: state.model }
  if (isNonBlank(state.apiKey)) {
    payload.apiKey = state.apiKey
  }
  const maxDecisions = Number(state.maxDecisions)
  if (state.maxDecisions !== undefined && state.maxDecisions !== null && state.maxDecisions !== ''
    && Number.isInteger(maxDecisions) && maxDecisions >= 1) {
    payload.maxDecisionsPerDay = maxDecisions
  }
  return payload
}

export function testRequestPayload(state) {
  const payload = { providerId: state.providerId, model: state.model }
  if (isNonBlank(state.apiKey)) {
    payload.apiKey = state.apiKey
  }
  return payload
}

export function formatProviderLabel(providerLabel) {
  return isNonBlank(providerLabel) ? `AI · ${providerLabel}` : 'AI'
}

// Returns pretty-printed JSON text when the value parses, otherwise the raw string unchanged. The result is
// plain text for a <pre> region; it is never evaluated or treated as HTML.
export function formatStoredJson(value) {
  if (value === null || value === undefined || value === '') {
    return ''
  }

  try {
    return JSON.stringify(JSON.parse(value), null, 2)
  } catch {
    return String(value)
  }
}

// Shapes the backend decision-quality summary for display: whole-percent rates and an activity flag that hides the
// strip for a trader that has never called a provider. Money is left numeric so the caller formats it with the shared
// money helper.
export function formatDecisionQuality(summary) {
  if (!summary || typeof summary !== 'object') {
    return null
  }

  const num = (value) => (typeof value === 'number' && Number.isFinite(value) ? value : 0)
  const rate = (value) => `${Math.round(num(value) * 100)}%`
  const callAttempts = num(summary.callAttempts)
  const proposedOrders = num(summary.proposedOrders)
  const executedBuyNotional = num(summary.executedBuyNotional)

  return {
    hasActivity: callAttempts > 0 || proposedOrders > 0 || executedBuyNotional > 0,
    callCompletion: rate(summary.callCompletionRate),
    completedCalls: num(summary.completedCalls),
    callAttempts,
    invalidJsonCalls: num(summary.invalidJsonCalls),
    proposalAcceptance: rate(summary.proposalAcceptanceRate),
    appliedOrders: num(summary.appliedOrders),
    proposedOrders,
    rejectedOrders: num(summary.rejectedOrders),
    executedBuyNotional,
  }
}

function parseStoredObject(value) {
  if (value === null || value === undefined || value === '') {
    return null
  }

  try {
    const parsed = typeof value === 'string' ? JSON.parse(value) : value
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : null
  } catch {
    return null
  }
}

function extractThinking(response) {
  const message = response?.choices?.[0]?.message
  if (!message || typeof message !== 'object') {
    return ''
  }

  const dedicatedReasoning = message.reasoning_content ?? message.reasoningContent
  if (isNonBlank(dedicatedReasoning)) {
    return dedicatedReasoning.trim()
  }

  if (!isNonBlank(message.content)) {
    return ''
  }

  const match = message.content.match(/<think>([\s\S]*?)<\/think>/i)
  return match ? match[1].trim() : ''
}

export function parseAiCallPresentation(responseBody, decisionJson) {
  const response = parseStoredObject(responseBody)
  const decision = parseStoredObject(decisionJson)
  const orders = Array.isArray(decision?.orders)
    ? decision.orders.filter((order) => order && typeof order === 'object'
      && Number.isInteger(order.companyId) && order.companyId > 0)
    : []

  return {
    thinking: extractThinking(response),
    summary: typeof decision?.summary === 'string' ? decision.summary : '',
    orders,
  }
}
