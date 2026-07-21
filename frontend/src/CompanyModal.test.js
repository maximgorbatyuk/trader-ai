import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { createServer } from 'vite'

const company = {
  id: 7,
  name: 'Ulster Holdings',
  industryId: 3,
  industryName: 'Artificial Intelligence',
  issuedSharesCount: 1_000,
  currentPrice: 25,
  priceChangePct: 0.0065,
  luldState: 'Normal',
  playerPosition: { shares: 125, ownershipPct: 0.125, marketValue: 3_125 },
  fundPosition: { shares: 40, ownershipPct: 0.04, marketValue: 1_000 },
}

async function renderModal(t, props = {}) {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())
  const { CompanyModal } = await server.ssrLoadModule('/src/CompanyModal.jsx')

  return renderToStaticMarkup(
    createElement(
      MemoryRouter,
      null,
      createElement(CompanyModal, {
        company,
        actorKind: 'player',
        onClose() {},
        ...props,
      }),
    ),
  )
}

test('renders the Player position from the company response', async (t) => {
  const markup = await renderModal(t)

  assert.match(markup, />Player position</)
  assert.doesNotMatch(markup, />Managed fund position</)
  assert.match(markup, />Shares owned<\/dt><dd class="num">125<\/dd>/)
  assert.match(markup, />Ownership<\/dt><dd class="num">12\.50%<\/dd>/)
  assert.match(markup, />Position value<\/dt><dd class="num">\$3,125\.00<\/dd>/)
})

test('renders the Managed fund position from the company response', async (t) => {
  const markup = await renderModal(t, { actorKind: 'fund' })

  assert.match(markup, />Managed fund position</)
  assert.doesNotMatch(markup, />Player position</)
  assert.match(markup, />Shares owned<\/dt><dd class="num">40<\/dd>/)
  assert.match(markup, />Ownership<\/dt><dd class="num">4\.00%<\/dd>/)
  assert.match(markup, />Position value<\/dt><dd class="num">\$1,000\.00<\/dd>/)
})

test('renders the required empty label when the selected actor owns no shares', async (t) => {
  const markup = await renderModal(t, {
    company: {
      ...company,
      fundPosition: { shares: 0, ownershipPct: 0, marketValue: 0 },
    },
    actorKind: 'fund',
  })

  assert.match(markup, />Managed fund position</)
  assert.match(markup, />No shares of this company<\/p>/)
  assert.doesNotMatch(markup, />Shares owned<\/dt>/)
})
