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

function put(path, body) {
  return request(path, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}

export const api = {
  getHealth: () => get('/health'),
  getMarket: () => get('/market'),
  getCompanies: () => get('/companies'),
  getCompany: (companyId) => get(`/companies/${companyId}`),
  getCompanyShareholders: (companyId) => get(`/companies/${companyId}/shareholders`),
  getCompanyOrders: (companyId, take = 10) => get(`/companies/${companyId}/orders?take=${take}`),
  getCompanyShareTransactions: (companyId, take = 10) =>
    get(`/companies/${companyId}/share-transactions?take=${take}`),
  getParticipants: () => get('/participants'),
  getOrders: (status) => get(status ? `/orders?status=${status}` : '/orders'),
  getCycles: () => get('/cycles'),
  getCycleActivity: () => get('/cycles/activity'),
  getShareTransactions: (take) => get(take ? `/transactions/shares?take=${take}` : '/transactions/shares'),
  getPrices: (companyId) => get(`/prices/${companyId}`),
  getNews: (take = 30) => get(`/news?take=${take}`),
  getCrises: (take = 30) => get(`/crises?take=${take}`),
  getScienceInvestigations: (take = 30) => get(`/science-investigations?take=${take}`),
  getBankruptcies: (take = 30) => get(`/bankruptcies?take=${take}`),
  getMarketExits: (take = 50) => get(`/market-exits?take=${take}`),
  getNewsThemes: () => get('/news/themes'),
  getIndustries: () => get('/industries'),
  createNews: (payload) => post('/news', payload),
  getHoldings: (participantId) => get(`/participants/${participantId}/holdings`),
  getParticipant: (participantId) => get(`/participants/${participantId}`),
  getParticipantOrders: (participantId, take = 10) => get(`/participants/${participantId}/orders?take=${take}`),
  getParticipantShareTransactions: (participantId, take = 10) =>
    get(`/participants/${participantId}/share-transactions?take=${take}`),
  getParticipantMoneyTransactions: (participantId, take = 10) =>
    get(`/participants/${participantId}/money-transactions?take=${take}`),
  updateParticipantProfile: (participantId, profile) => put(`/participants/${participantId}/profile`, profile),
  seedMarket: () => post('/market/seed'),
  resetMarket: () => post('/market/reset'),
  pauseMarket: () => post('/market/pause'),
  startMarket: () => post('/market/start'),
  stepCycle: () => post('/cycles/tick'),
  placeOrder: (order) => post('/orders', order),
  getPlayer: () => get('/player'),
  createPlayer: (payload) => post('/player', payload),
  cancelPlayerOrder: (orderId) => post(`/player/orders/${orderId}/cancel`),
}
