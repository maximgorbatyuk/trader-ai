const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5100').replace(/\/$/, '')

async function request(path, options) {
  const response = await fetch(`${API_BASE_URL}${path}`, options)

  if (!response.ok) {
    let message = `Request failed (${response.status})`
    try {
      const body = await response.json()
      if (body?.error) {
        message = body.error
      }
    } catch {
      // Response had no JSON error body; keep the status-based message.
    }

    throw new Error(message)
  }

  const text = await response.text()
  return text ? JSON.parse(text) : null
}

function get(path) {
  return request(path)
}

function post(path, body) {
  return request(path, {
    method: 'POST',
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  })
}

export const api = {
  getHealth: () => get('/health'),
  getMarket: () => get('/market'),
  getCompanies: () => get('/companies'),
  getParticipants: () => get('/participants'),
  getOrders: (status) => get(status ? `/orders?status=${status}` : '/orders'),
  getCycles: () => get('/cycles'),
  getShareTransactions: (take) => get(take ? `/transactions/shares?take=${take}` : '/transactions/shares'),
  getPrices: (companyId) => get(`/prices/${companyId}`),
  seedMarket: () => post('/market/seed'),
  pauseMarket: () => post('/market/pause'),
  startMarket: () => post('/market/start'),
  advanceCycle: () => post('/cycles/advance'),
  runDecisions: () => post('/decisions/run'),
  placeOrder: (order) => post('/orders', order),
}
