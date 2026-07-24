import assert from 'node:assert/strict'
import test, { after, before } from 'node:test'
import { createElement, isValidElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'

let server
let NewsFeedPost
let NewsSelectionModal

before(async () => {
  server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  ;({ NewsFeedPost, NewsSelectionModal } = await server.ssrLoadModule('/src/NewsPage.jsx'))
})

after(() => server?.close())

function findElement(node, predicate) {
  if (Array.isArray(node)) {
    for (const child of node) {
      const match = findElement(child, predicate)
      if (match) return match
    }
    return null
  }
  if (!isValidElement(node)) return null
  if (predicate(node)) return node
  return findElement(node.props.children, predicate)
}

const ordinaryNews = {
  id: 1,
  title: 'Ordinary market update',
  content: 'This post keeps the existing news detail dialog.',
  publishedInCycleNumber: 99,
  category: 'General',
  scope: 'None',
  direction: null,
  industryNames: [],
  portfolioAuditSummaryId: null,
}

test('routes a structured summary id to the portfolio modal and ordinary news to the existing modal', () => {
  const selectedNews = []
  const selectedSummaries = []
  const portfolioPost = {
    ...ordinaryNews,
    id: 2,
    title: 'Held-company audit summary',
    category: 'General',
    portfolioAuditSummaryId: 73,
  }
  const portfolioTree = NewsFeedPost({
    post: portfolioPost,
    onSelectNews(post) {
      selectedNews.push(post)
    },
    onSelectPortfolioAuditSummary(summaryId) {
      selectedSummaries.push(summaryId)
    },
  })
  const portfolioButton = findElement(
    portfolioTree,
    (element) => element.type === 'button' && element.props['data-portfolio-audit-summary-id'] === 73,
  )
  assert.ok(portfolioButton)
  portfolioButton.props.onClick()
  assert.deepEqual(selectedSummaries, [73])
  assert.deepEqual(selectedNews, [])

  const ordinaryTree = NewsFeedPost({
    post: { ...ordinaryNews, category: 'PortfolioAudit' },
    onSelectNews(post) {
      selectedNews.push(post)
    },
    onSelectPortfolioAuditSummary(summaryId) {
      selectedSummaries.push(summaryId)
    },
  })
  const ordinaryButton = findElement(
    ordinaryTree,
    (element) => element.type === 'button' && element.props.title === 'Open Ordinary market update',
  )
  assert.ok(ordinaryButton)
  ordinaryButton.props.onClick()
  assert.equal(selectedNews.length, 1)
  assert.equal(selectedNews[0].id, 1)
  assert.deepEqual(selectedSummaries, [73])
})

test('preserves the ordinary news modal while selecting the shared portfolio summary modal by id', () => {
  const ordinaryMarkup = renderToStaticMarkup(
    createElement(NewsSelectionModal, {
      selectedNews: ordinaryNews,
      selectedPortfolioAuditSummaryId: null,
      onCloseNews() {},
      onClosePortfolioAuditSummary() {},
    }),
  )
  assert.match(ordinaryMarkup, /role="dialog"/)
  assert.match(ordinaryMarkup, />Ordinary market update</)
  assert.doesNotMatch(ordinaryMarkup, /modal-portfolio-audit/)

  const portfolioMarkup = renderToStaticMarkup(
    createElement(NewsSelectionModal, {
      selectedNews: null,
      selectedPortfolioAuditSummaryId: 73,
      onCloseNews() {},
      onClosePortfolioAuditSummary() {},
    }),
  )
  assert.match(portfolioMarkup, /class="modal modal-portfolio-audit"/)
  assert.match(portfolioMarkup, /Portfolio audit summary #73/)
  assert.match(portfolioMarkup, /Loading portfolio audit summary…/)
})
