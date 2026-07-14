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

function remove(path) {
  return request(path, { method: 'DELETE' })
}

// Drops null/undefined/empty values so optional filters never reach the API as blank query params.
function toQuery(params) {
  const query = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== null && value !== undefined && value !== '') {
      query.set(key, value)
    }
  }
  const text = query.toString()
  return text ? `?${text}` : ''
}

export const api = {
  getHealth: () => get('/health'),
  getMarket: () => get('/market'),
  getCompanies: () => get('/companies'),
  getCompaniesPaged: (params = {}) => get(`/companies/paged${toQuery(params)}`),
  getCompany: (companyId) => get(`/companies/${companyId}`),
  getCompanyNews: (companyId, take = 20) => get(`/companies/${companyId}/news?take=${take}`),
  getCompanyShareholders: (companyId) => get(`/companies/${companyId}/shareholders`),
  getCompanyOrders: (companyId, take = 10) => get(`/companies/${companyId}/orders?take=${take}`),
  getCompanyShareTransactions: (companyId, take = 10) =>
    get(`/companies/${companyId}/share-transactions?take=${take}`),
  getCompanyRatings: (companyId, take = 20) => get(`/companies/${companyId}/ratings?take=${take}`),
  getCompanyEmissions: (companyId, take = 20) => get(`/companies/${companyId}/emissions?take=${take}`),
  getCompanyCorporateCashMovements: (companyId, page = 1, pageSize = 10) =>
    get(`/companies/${companyId}/corporate-cash-movements?page=${page}&pageSize=${pageSize}`),
  getAuditors: () => get('/auditors'),
  getAuditor: (auditorId) => get(`/auditors/${auditorId}`),
  getAuditorAudits: (auditorId, page = 1, pageSize = 20) =>
    get(`/auditors/${auditorId}/audits?page=${page}&pageSize=${pageSize}`),
  getParticipants: () => get('/participants'),
  getParticipantsPaged: (params = {}) => get(`/participants/paged${toQuery(params)}`),
  getOrders: (status) => get(status ? `/orders?status=${status}` : '/orders'),
  getCycles: () => get('/cycles'),
  getCycleActivity: () => get('/cycles/activity'),
  getShareTransactions: (take) => get(take ? `/transactions/shares?take=${take}` : '/transactions/shares'),
  getPrices: (companyId) => get(`/prices/${companyId}`),
  getNews: (take = 30) => get(`/news?take=${take}`),
  getNewsPaged: (page = 1, pageSize = 20) => get(`/news/paged?page=${page}&pageSize=${pageSize}`),
  getCrises: (take = 30) => get(`/crises?take=${take}`),
  getCrisis: (crisisId) => get(`/crises/${crisisId}`),
  getScienceInvestigations: (take = 30) => get(`/science-investigations?take=${take}`),
  getBankruptcies: (take = 30) => get(`/bankruptcies?take=${take}`),
  getMarketExits: (take = 50) => get(`/market-exits?take=${take}`),
  getClosedFunds: (page = 1, pageSize = 20) => get(`/collective-funds/closed?page=${page}&pageSize=${pageSize}`),
  getClosedCompanies: (page = 1, pageSize = 20) => get(`/companies/closed?page=${page}&pageSize=${pageSize}`),
  getBanks: () => get('/banks'),
  getLoansPaged: (params = {}) => get(`/loans/paged${toQuery(params)}`),
  getParticipantLoans: (participantId, { status } = {}) =>
    get(`/participants/${participantId}/loans${toQuery({ status })}`),
  repayLoan: (loanId, amount) => post(`/loans/${loanId}/repay`, amount != null ? { amount } : undefined),
  getNewsThemes: (scope) => get(`/news/themes${toQuery({ scope })}`),
  getIndustries: () => get('/industries'),
  getIndustry: (industryId) => get(`/industries/${industryId}`),
  getIndustrySentimentHistory: (industryId) => get(`/industries/${industryId}/sentiment-history`),
  getIndustriesSentimentHistory: () => get('/industries/sentiment-history'),
  getIndustryNews: (industryId, take = 20) => get(`/industries/${industryId}/news?take=${take}`),
  createNews: (payload) => post('/news', payload),
  getHoldings: (participantId) => get(`/participants/${participantId}/holdings`),
  getCompaniesAttention: (participantId) => get(`/participants/${participantId}/companies-attention`),
  getParticipant: (participantId) => get(`/participants/${participantId}`),
  getParticipantOrders: (participantId, take = 10) => get(`/participants/${participantId}/orders?take=${take}`),
  getParticipantShareTransactions: (participantId, take = 10) =>
    get(`/participants/${participantId}/share-transactions?take=${take}`),
  getParticipantSettlements: (participantId, { status = 'pending', page = 1, pageSize = 100 } = {}) =>
    get(`/participants/${participantId}/settlements${toQuery({ status, page, pageSize })}`),
  getParticipantMoneyTransactions: (participantId, take = 10) =>
    get(`/participants/${participantId}/money-transactions?take=${take}`),
  getMoneyTransactionDetail: (participantId, transactionId) =>
    get(`/participants/${participantId}/money-transactions/${transactionId}`),
  getParticipantWorthHistory: (participantId, take) =>
    get(take ? `/participants/${participantId}/worth-history?take=${take}` : `/participants/${participantId}/worth-history`),
  getFundMembershipHistory: (participantId, page = 1, pageSize = 10) =>
    get(`/participants/${participantId}/fund-membership-history?page=${page}&pageSize=${pageSize}`),
  updateParticipantProfile: (participantId, profile) => put(`/participants/${participantId}/profile`, profile),
  getAiProviders: () => get('/ai/providers'),
  updateParticipantAutomation: (participantId, payload) => put(`/participants/${participantId}/automation`, payload),
  testParticipantAutomation: (participantId, payload) => post(`/participants/${participantId}/automation/test`, payload),
  getParticipantAiCalls: (participantId, page = 1, pageSize = 20) =>
    get(`/participants/${participantId}/ai-calls${toQuery({ page, pageSize })}`),
  getParticipantAiCall: (participantId, callId) => get(`/participants/${participantId}/ai-calls/${callId}`),
  seedMarket: () => post('/market/seed'),
  resetMarket: () => post('/market/reset'),
  pauseMarket: () => post('/market/pause'),
  startMarket: () => post('/market/start'),
  stepCycle: () => post('/cycles/tick'),
  placeOrder: (order) => post('/orders', order),
  getPlayer: () => get('/player'),
  createPlayer: (payload) => post('/player', payload),
  markFavoriteCompany: (companyId) => put(`/player/favorite-companies/${companyId}`, {}),
  unmarkFavoriteCompany: (companyId) => remove(`/player/favorite-companies/${companyId}`),
  cancelPlayerOrder: (orderId) => post(`/player/orders/${orderId}/cancel`),
  openPlayerFund: (payload) => post('/player/fund', payload),
  depositToPlayerFund: (payload) => post('/player/fund/deposit', payload),
  withdrawFromPlayerFund: (payload) => post('/player/fund/withdraw', payload),
  closePlayerFund: () => post('/player/fund/close'),
  getFundAdvertiseQuote: (fundParticipantId) => get(`/funds/${fundParticipantId}/advertise-quote`),
  advertiseFund: (fundParticipantId) => post(`/funds/${fundParticipantId}/advertise`),
}
