import assert from 'node:assert/strict'
import test from 'node:test'
import { readFile } from 'node:fs/promises'
import { createElement, isValidElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { createServer } from 'vite'

const news = [
  {
    id: 1,
    title: 'Newest regular news',
    content: 'The newest regular market update.',
    publishedInCycleNumber: 99,
    category: 'General',
    scope: 'None',
    direction: null,
    industryNames: [],
  },
  {
    id: 2,
    title: 'Second regular news',
    content: 'The second regular market update.',
    publishedInCycleNumber: 98,
    category: 'General',
    scope: 'None',
    direction: null,
    industryNames: [],
  },
]

async function renderLatestNews(t, props) {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { LatestNews } = await server.ssrLoadModule('/src/LatestNews.jsx')
  return renderToStaticMarkup(
    createElement(MemoryRouter, null, createElement(LatestNews, props)),
  )
}

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

test('replaces one news card with the newest recent science or crisis event', async (t) => {
  const markup = await renderLatestNews(t, {
    news,
    currentCycleNumber: 100,
    crises: [
      {
        id: 7,
        title: 'Recent crisis',
        content: 'A recent crisis hit the market.',
        scope: 'Local',
        triggeredInCycleNumber: 94,
        industries: [{ impactPercent: 4.5 }],
      },
    ],
    scienceInvestigations: [
      {
        id: 9,
        title: 'New science lift',
        content: 'A science breakthrough lifted two sectors.',
        triggeredInCycleNumber: 96,
        industries: [{ impactPercent: 2.5 }, { impactPercent: 4 }],
      },
    ],
  })
  const cards = markup.match(/<div class="map-news(?: |")/g) ?? []

  assert.equal(cards.length, 2)
  assert.match(markup, /Science breakthrough/)
  assert.match(markup, /New science lift/)
  assert.match(markup, /Newest regular news/)
  assert.doesNotMatch(markup, /Recent crisis/)
  assert.doesNotMatch(markup, /Second regular news/)
  assert.ok(markup.indexOf('New science lift') < markup.indexOf('Newest regular news'))
})

test('links a newer recent crisis to its detail page', async (t) => {
  const markup = await renderLatestNews(t, {
    news,
    currentCycleNumber: 100,
    crises: [
      {
        id: 7,
        title: 'Newest crisis',
        content: 'A crisis displaced one regular news card.',
        scope: 'Global',
        triggeredInCycleNumber: 97,
        industries: [{ impactPercent: 7.25 }],
      },
    ],
    scienceInvestigations: [
      {
        id: 9,
        title: 'Older science lift',
        content: 'A science event happened first.',
        triggeredInCycleNumber: 96,
        industries: [{ impactPercent: 2.5 }],
      },
    ],
  })

  assert.match(markup, /Global crisis/)
  assert.match(markup, /href="\/crises\/7"[^>]*>Newest crisis<\/a>/)
  assert.doesNotMatch(markup, /Older science lift/)
})

test('keeps two regular news cards when market events are older than fifteen cycles', async (t) => {
  const markup = await renderLatestNews(t, {
    news,
    currentCycleNumber: 100,
    crises: [
      {
        id: 7,
        title: 'Old crisis',
        content: 'This crisis is no longer recent.',
        scope: 'Local',
        triggeredInCycleNumber: 84,
        industries: [{ impactPercent: 4.5 }],
      },
    ],
    scienceInvestigations: [
      {
        id: 9,
        title: 'Old science lift',
        content: 'This science event is no longer recent.',
        triggeredInCycleNumber: 80,
        industries: [{ impactPercent: 2.5 }],
      },
    ],
  })

  assert.match(markup, /Newest regular news/)
  assert.match(markup, /Second regular news/)
  assert.doesNotMatch(markup, /Old crisis/)
  assert.doesNotMatch(markup, /Old science lift/)
})

test('uses only the structured summary id to make a portfolio audit card actionable', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { LatestNews } = await server.ssrLoadModule('/src/LatestNews.jsx')
  const { portfolioAuditSummaryId } = await server.ssrLoadModule('/src/newsCategory.js')
  assert.equal(typeof portfolioAuditSummaryId, 'function')
  assert.equal(portfolioAuditSummaryId({ category: 'PortfolioAudit' }), null)
  assert.equal(
    portfolioAuditSummaryId({ category: 'General', portfolioAuditSummaryId: 73 }),
    73,
  )

  const selected = []
  const auditNews = {
    ...news[0],
    id: 8,
    title: 'Held-company audit summary',
    category: 'General',
    portfolioAuditSummaryId: 73,
  }
  const spoofedNews = {
    ...news[1],
    id: 9,
    title: 'Portfolio audit update without structured data',
    category: 'PortfolioAudit',
    portfolioAuditSummaryId: null,
  }
  const props = {
    news: [auditNews, spoofedNews],
    currentCycleNumber: 100,
    onSelectPortfolioAuditSummary(summaryId) {
      selected.push(summaryId)
    },
  }
  const tree = LatestNews(props)
  const action = findElement(
    tree,
    (element) => element.props['data-portfolio-audit-summary-id'] === 73 && element.props.role === 'button',
  )
  assert.ok(action)
  action.props.onClick()
  action.props.onKeyDown({ key: 'Enter', preventDefault() {} })
  action.props.onKeyDown({ key: ' ', preventDefault() {} })
  action.props.onKeyDown({ key: 'ArrowDown', preventDefault() {} })
  assert.deepEqual(selected, [73, 73, 73])

  const markup = renderToStaticMarkup(createElement(MemoryRouter, null, tree))
  assert.match(markup, /data-portfolio-audit-summary-id="73"/)
  assert.match(markup, /aria-label="Open portfolio audit summary: Held-company audit summary"/)
  assert.match(markup, /role="button" tabindex="0"/)
  assert.doesNotMatch(markup, /data-portfolio-audit-summary-id="null"/)
  assert.match(markup, /<p class="map-news-title">Portfolio audit update without structured data<\/p>/)

  const css = await readFile(new URL('./App.css', import.meta.url), 'utf8')
  assert.match(css, /\.map-news-action:focus-visible/)
})
