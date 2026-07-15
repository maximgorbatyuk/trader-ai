import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
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
