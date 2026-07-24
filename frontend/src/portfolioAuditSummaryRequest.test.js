import assert from 'node:assert/strict'
import test from 'node:test'
import { createPortfolioAuditSummaryRequestCoordinator } from './portfolioAuditSummaryRequest.js'

function deferred() {
  let resolve
  let reject
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

function coordinatorWith(overrides = {}) {
  return createPortfolioAuditSummaryRequestCoordinator({
    summaryId: 61,
    request: async () => ({ id: 61 }),
    onLoading() {},
    onSuccess() {},
    onError() {},
    ...overrides,
  })
}

test('one initial load issues one request and publishes its result', async () => {
  const events = []
  let requestCount = 0
  const coordinator = coordinatorWith({
    request: async (summaryId) => {
      requestCount += 1
      events.push(`request:${summaryId}`)
      return { id: summaryId }
    },
    onLoading() {
      events.push('loading')
    },
    onSuccess(summary) {
      events.push(`success:${summary.id}`)
    },
  })

  await coordinator.load()

  assert.equal(requestCount, 1)
  assert.deepEqual(events, ['loading', 'request:61', 'success:61'])
})

test('retry restores dialog focus before starting exactly one replacement request', async () => {
  const events = []
  let requestCount = 0
  const coordinator = coordinatorWith({
    request: async () => {
      requestCount += 1
      events.push('request')
      return { id: 61 }
    },
    onLoading() {
      events.push('loading')
    },
  })

  await coordinator.load()
  events.length = 0
  await coordinator.retry(() => events.push('focus'))

  assert.equal(requestCount, 2)
  assert.deepEqual(events, ['focus', 'loading', 'request'])
})

test('a later request wins when an earlier response arrives stale', async () => {
  const pending = []
  const published = []
  const coordinator = coordinatorWith({
    request: () => {
      const request = deferred()
      pending.push(request)
      return request.promise
    },
    onSuccess(summary) {
      published.push(summary.id)
    },
  })

  const firstLoad = coordinator.load()
  const retry = coordinator.retry()
  pending[1].resolve({ id: 62 })
  await retry
  pending[0].resolve({ id: 61 })
  await firstLoad

  assert.deepEqual(published, [62])
})

test('the current request publishes its error without rejecting the coordinator', async () => {
  const errors = []
  const coordinator = coordinatorWith({
    request: async () => {
      throw new Error('Summary unavailable')
    },
    onError(error) {
      errors.push(error.message)
    },
  })

  await coordinator.load()

  assert.deepEqual(errors, ['Summary unavailable'])
})

test('dispose suppresses completion callbacks from an in-flight request', async () => {
  const request = deferred()
  const events = []
  const coordinator = coordinatorWith({
    request: () => request.promise,
    onSuccess() {
      events.push('success')
    },
    onError() {
      events.push('error')
    },
  })

  const load = coordinator.load()
  coordinator.dispose()
  request.resolve({ id: 61 })
  await load

  assert.deepEqual(events, [])
})
