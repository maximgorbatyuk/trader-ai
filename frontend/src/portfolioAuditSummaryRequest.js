export function createPortfolioAuditSummaryRequestCoordinator({
  summaryId,
  request,
  onLoading,
  onSuccess,
  onError,
}) {
  let activeRequest = 0
  let disposed = false

  function load() {
    if (disposed) return Promise.resolve()

    const requestId = activeRequest + 1
    activeRequest = requestId
    onLoading()

    let pending
    try {
      pending = request(summaryId)
    } catch (error) {
      if (!disposed && activeRequest === requestId) onError(error)
      return Promise.resolve()
    }

    return Promise.resolve(pending).then(
      (summary) => {
        if (!disposed && activeRequest === requestId) onSuccess(summary)
      },
      (error) => {
        if (!disposed && activeRequest === requestId) onError(error)
      },
    )
  }

  function retry(focusBeforeRequest) {
    focusBeforeRequest?.()
    return load()
  }

  function dispose() {
    disposed = true
    activeRequest += 1
  }

  return { load, retry, dispose }
}
