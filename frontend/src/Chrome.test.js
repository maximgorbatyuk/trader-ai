import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { createServer } from 'vite'

test('renders AI provider usage links in an accessible footer group', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { Footer } = await server.ssrLoadModule('/src/Chrome.jsx')
  const markup = renderToStaticMarkup(createElement(Footer))

  assert.match(markup, /aria-label="AI provider usage"/)
  assert.ok(markup.includes('<a href="https://platform.minimax.io/console/usage" target="_blank" rel="noreferrer">MiniMax</a>'))
  assert.ok(markup.includes('<a href="https://z.ai/manage-apikey/coding-plan/personal/usage" target="_blank" rel="noreferrer">GLM</a>'))
  assert.ok(markup.includes('<a href="https://platform.openai.com/usage" target="_blank" rel="noreferrer">OpenAI</a>'))
  assert.ok(markup.includes('<a href="https://platform.claude.com/usage" target="_blank" rel="noreferrer">Claude</a>'))
})

test('renders an accessible settings cog in the top navbar', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { TopBar } = await server.ssrLoadModule('/src/Chrome.jsx')
  const markup = renderToStaticMarkup(
    createElement(MemoryRouter, { initialEntries: ['/settings'] },
      createElement(TopBar, {
        connected: true,
        ready: true,
        market: null,
        pending: false,
        tradingClock: null,
        runAction() {},
        resetMarket() {},
      }),
    ),
  )

  assert.match(markup, /href="\/settings"/)
  assert.match(markup, /aria-label="Settings"/)
  assert.match(markup, /topbar-settings-link is-active/)
  assert.match(markup, /<svg[^>]*aria-hidden="true"/)
})
