// Pure helpers for the AI-trader automation UI. These hold the client-side rules for provider and model entry
// and formatting; the backend remains authoritative for validation. The connection key is a per-provider setting
// and is not entered here. Nothing here evaluates or renders HTML.

function isNonBlank(value) {
  return typeof value === 'string' && value.trim().length > 0
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
  const maxDecisions = Number(state.maxDecisions)
  if (state.maxDecisions !== undefined && state.maxDecisions !== null && state.maxDecisions !== ''
    && Number.isInteger(maxDecisions) && maxDecisions >= 1) {
    payload.maxDecisionsPerDay = maxDecisions
  }
  return payload
}

export function testRequestPayload(state) {
  return { providerId: state.providerId, model: state.model }
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

export function parseAiCallPresentation(responseBody, decisionJson, applicationResultJson) {
  const response = parseStoredObject(responseBody)
  const decision = parseStoredObject(decisionJson)
  const application = parseStoredObject(applicationResultJson)
  const orders = Array.isArray(decision?.orders)
    ? decision.orders.filter((order) => order && typeof order === 'object'
      && Number.isInteger(order.companyId) && order.companyId > 0)
    : []
  const investment = decision?.bigInvestment
  const bigInvestment = investment && typeof investment === 'object'
    && Number.isInteger(investment.companyId) && investment.companyId > 0
    && typeof investment.amount === 'number' && Number.isFinite(investment.amount) && investment.amount > 0
      ? investment
      : null
  const investmentApplication = application?.bigInvestment
  const bigInvestmentApplication = investmentApplication && typeof investmentApplication === 'object'
    && Number.isInteger(investmentApplication.companyId) && investmentApplication.companyId > 0
    && typeof investmentApplication.amount === 'number' && Number.isFinite(investmentApplication.amount)
    && typeof investmentApplication.applied === 'boolean'
    && Number.isInteger(investmentApplication.sharesMinted) && investmentApplication.sharesMinted >= 0
      ? investmentApplication
      : null

  return {
    thinking: extractThinking(response),
    summary: typeof decision?.summary === 'string' ? decision.summary : '',
    orders,
    bigInvestment,
    bigInvestmentApplication,
  }
}
