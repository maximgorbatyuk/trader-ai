import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'

test('renders the settings route with an honest loading state', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { SettingsPage } = await server.ssrLoadModule('/src/SettingsPage.jsx')
  const markup = renderToStaticMarkup(createElement(SettingsPage))

  assert.match(markup, /<main class="main settings-page">/)
  assert.match(markup, /<h1>Settings<\/h1>/)
  assert.match(markup, /role="status"/)
  assert.match(markup, /Loading settings/)
})

test('renders cross-field validation details in an accessible summary', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { ValidationSummary } = await server.ssrLoadModule('/src/SettingsPage.jsx')
  const markup = renderToStaticMarkup(createElement(ValidationSummary, {
    fieldErrors: {
      TradingClock: ['Trading durations must be positive.'],
    },
  }))

  assert.match(markup, /TradingClock/)
  assert.match(markup, /Trading durations must be positive/)
})

test('renders the relocated project and AI provider usage links', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { SettingsLinks } = await server.ssrLoadModule('/src/SettingsPage.jsx')
  const markup = renderToStaticMarkup(createElement(SettingsLinks))

  assert.match(markup, /aria-label="AI provider usage"/)
  assert.ok(markup.includes('<a href="https://platform.minimax.io/console/usage" target="_blank" rel="noreferrer">MiniMax</a>'))
  assert.ok(markup.includes('<a href="https://z.ai/manage-apikey/coding-plan/personal/usage" target="_blank" rel="noreferrer">GLM</a>'))
  assert.ok(markup.includes('<a href="https://platform.openai.com/usage" target="_blank" rel="noreferrer">OpenAI</a>'))
  assert.ok(markup.includes('<a href="https://platform.claude.com/usage" target="_blank" rel="noreferrer">Claude</a>'))
  assert.match(markup, /aria-label="Project links"/)
  assert.match(markup, /aria-label="Repository links"/)
  assert.match(markup, />Concept</)
  assert.match(markup, />Github</)
  assert.match(markup, />Issues</)
})
