import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter, Outlet, Route, Routes } from 'react-router-dom'
import { createServer } from 'vite'

test('places filled orders directly after the live order book', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { default: TradeMarketPage } = await server.ssrLoadModule('/src/TradeMarketPage.jsx')
  const shell = createElement(Outlet, { context: { market: null, actorKind: 'player' } })
  const page = createElement(
    MemoryRouter,
    { initialEntries: ['/trade-market'] },
    createElement(
      Routes,
      null,
      createElement(
        Route,
        { element: shell },
        createElement(Route, { path: '/trade-market', element: createElement(TradeMarketPage) }),
      ),
    ),
  )

  const markup = renderToStaticMarkup(page)
  const orderBookIndex = markup.indexOf('Order book')
  const filledOrdersIndex = markup.indexOf('Filled orders / settlements')

  assert.ok(orderBookIndex >= 0)
  assert.ok(filledOrdersIndex > orderBookIndex)
  assert.match(markup, /No orders have been filled yet\./)
})
