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
