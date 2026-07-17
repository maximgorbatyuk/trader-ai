import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { createServer } from 'vite'

test('renders compact ownership metrics before the paginated shareholder table', async (t) => {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const companyDetailModule = await server.ssrLoadModule('/src/CompanyDetail.jsx')
  assert.equal(typeof companyDetailModule.ShareholdersPanel, 'function')

  const shareholders = Array.from({ length: 11 }, (_, index) => ({
    ownerId: index + 1,
    ownerName: `Owner ${index + 1}`,
    shares: index + 1,
    pctOfIssued: (index + 1) / 1_000,
    marketValue: (index + 1) * 25,
  }))
  const detail = {
    issuedSharesCount: 1_000,
    sharesHeldByIssuer: 100,
    sharesOutstanding: 900,
    shareholderCount: 11,
  }

  const markup = renderToStaticMarkup(
    createElement(
      MemoryRouter,
      null,
      createElement(companyDetailModule.ShareholdersPanel, { shareholders, detail }),
    ),
  )
  const ownershipIndex = markup.indexOf('class="ownership-summary"')
  const tableIndex = markup.indexOf('class="tbl-wrap"')
  const pagerIndex = markup.indexOf('class="pager"')
  const metrics = markup.match(/class="ownership-metric"/g) ?? []
  const renderedOwners = markup.match(/class="cell-link"/g) ?? []

  assert.equal(metrics.length, 5)
  // The shareholder page size now adapts to viewport height, so page one shows a fit-dependent subset rather
  // than a fixed ten; assert the table paged (fewer than all eleven owners) instead of a specific count.
  assert.ok(renderedOwners.length > 0 && renderedOwners.length < 11)
  assert.match(markup, /class="ownership-metrics"/)
  assert.match(markup, />Owner 11<\/a>/)
  assert.doesNotMatch(markup, />Owner 1<\/a>/)
  assert.ok(ownershipIndex >= 0)
  assert.ok(tableIndex > ownershipIndex)
  assert.ok(pagerIndex > tableIndex)
})
