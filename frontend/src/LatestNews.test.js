import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
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
