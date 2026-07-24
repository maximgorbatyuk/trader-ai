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
  let disposed = false

  return {
    setActiveQuery(query) {
      if (disposed) return
      activeQueryKey = financialHistoryQueryKey(query)
    },
    refresh(query) {
      if (disposed) return null

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
            if (!disposed && activeQueryKey === queryKey) onSuccess(data, query)
            return data
          },
          (error) => {
            if (!disposed && activeQueryKey === queryKey) onError(error, query)
            return null
          },
        )
        .finally(() => {
          if (inFlight.get(queryKey) === pending) inFlight.delete(queryKey)
        })
      inFlight.set(queryKey, pending)
      return pending
    },
    dispose() {
      disposed = true
      activeQueryKey = null
      inFlight.clear()
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
