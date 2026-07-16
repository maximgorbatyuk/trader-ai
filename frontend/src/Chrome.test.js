import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { createServer } from 'vite'

test('renders accessible About and Settings icon links in the top navbar', async (t) => {
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
        market: null,
        pending: false,
        tradingClock: null,
        runAction() {},
      }),
    ),
  )

  assert.match(markup, /href="\/about"/)
  assert.match(markup, /aria-label="About"/)
  assert.match(markup, /href="\/settings"/)
  assert.match(markup, /aria-label="Settings"/)
  assert.match(markup, /topbar-icon-link is-active/)
  assert.match(markup, /<svg[^>]*aria-hidden="true"/)
})
