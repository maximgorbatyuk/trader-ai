import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'
import { aboutDocumentKeyForHref, aboutTabKeyAfterKeyDown } from './aboutPageModel.js'

test('renders every domain document as an accessible tab', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { AboutPage } = await server.ssrLoadModule('/src/AboutPage.jsx')
  const markup = renderToStaticMarkup(createElement(AboutPage))
  const tabs = markup.match(/role="tab"/g) ?? []
  const controlledTabs = markup.match(/aria-controls=/g) ?? []

  assert.match(markup, /<main class="main about-page">/)
  assert.match(markup, /<h1>About<\/h1>/)
  assert.match(markup, /role="tablist" aria-label="About documentation"/)
  assert.equal(tabs.length, 22)
  assert.equal(controlledTabs.length, 1)
  assert.match(markup, /aria-selected="true"[^>]*>Domain<\/button>/)
  assert.doesNotMatch(markup, />Architecture<\/button>/)
})

test('renders the active Markdown document as in-page help', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { AboutPage } = await server.ssrLoadModule('/src/AboutPage.jsx')
  const markup = renderToStaticMarkup(createElement(AboutPage))

  assert.match(markup, /<h2>Domain<\/h2>/)
  assert.match(markup, /<h3>Core Rules<\/h3>/)
  assert.match(markup, /<h4>Participant<\/h4>/)
  assert.match(markup, /The app simulates a trading market\./)
  assert.match(markup, /<button type="button" class="about-doc-link">Corporate cash<\/button>/)
})

test('resolves linked documents and keyboard tab destinations', () => {
  assert.equal(aboutDocumentKeyForHref('logic/corporate-cash.md', 'domain.md'), 'corporate-cash')
  assert.equal(aboutDocumentKeyForHref('../roles/player.md', 'logic/margin.md'), 'player')
  assert.equal(aboutDocumentKeyForHref('https://example.com/help.md', 'domain.md'), null)

  assert.equal(aboutTabKeyAfterKeyDown('domain', 'ArrowLeft'), 'behavioral-audit')
  assert.equal(aboutTabKeyAfterKeyDown('domain', 'ArrowRight'), 'participant-rules')
  assert.equal(aboutTabKeyAfterKeyDown('player', 'Home'), 'domain')
  assert.equal(aboutTabKeyAfterKeyDown('domain', 'End'), 'behavioral-audit')
  assert.equal(aboutTabKeyAfterKeyDown('domain', 'Enter'), null)
})
