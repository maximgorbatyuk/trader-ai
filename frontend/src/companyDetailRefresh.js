function financialHistoryQueryKey({ companyId, page, pageSize }) {
  return `${companyId}:${page}:${pageSize}`
}

export function createFinancialHistoryQueryLoader({
  request,
  onStart = () => {},
  onSuccess = () => {},
  onError = () => {},
}) {
  const inFlight = new Map()
  let activeQueryKey = null

  return {
    setActiveQuery(query) {
      activeQueryKey = financialHistoryQueryKey(query)
    },
    refresh(query) {
      const queryKey = financialHistoryQueryKey(query)
      const existing = inFlight.get(queryKey)
      if (existing) return existing

      onStart(query)
      let requestPromise
      try {
        requestPromise = Promise.resolve(request(query.companyId, query.page, query.pageSize))
      } catch (error) {
        requestPromise = Promise.reject(error)
      }
      const pending = requestPromise
        .then(
          (data) => {
            if (activeQueryKey === queryKey) onSuccess(data, query)
            return data
          },
          (error) => {
            if (activeQueryKey === queryKey) onError(error, query)
            return null
          },
        )
        .finally(() => {
          if (inFlight.get(queryKey) === pending) inFlight.delete(queryKey)
        })
      inFlight.set(queryKey, pending)
      return pending
    },
  }
}

export function refreshCompanyDetailRequests({
  activeTab,
  includeBase = false,
  refreshBase,
  refreshFinancialHistory,
  refreshAuditHistory,
}) {
  const requests = []
  if (includeBase) requests.push(refreshBase())
  if (activeTab === 'financial-history') requests.push(refreshFinancialHistory())
  if (activeTab === 'audits') requests.push(refreshAuditHistory())
  return Promise.all(requests)
}
