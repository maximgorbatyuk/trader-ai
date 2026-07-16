import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'

async function renderTable(t, props) {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  const { FilledOrdersTable } = await server.ssrLoadModule('/src/FilledOrdersTable.jsx')
  return renderToStaticMarkup(createElement(FilledOrdersTable, props))
}

test('renders filled orders with counterparties, settlement state, and paging', async (t) => {
  const markup = await renderTable(t, {
    transactions: [{
      id: 9,
      sellerId: 10,
      buyerId: 20,
      companyId: 30,
      quantity: 125,
      price: 12.5,
      totalCost: 1562.5,
      tradeDayNumber: 7,
      dueDayNumber: 8,
      settlementStatus: 'Pending',
    }],
    total: 3,
    page: 1,
    pageSize: 1,
    participantNameById: new Map([[10, 'Seller A'], [20, 'Buyer B']]),
    companyNameById: new Map([[30, 'Company C']]),
    onPage() {},
    onSelectCompany() {},
  })

  assert.match(markup, /aria-label="Filled orders and settlements"/)
  assert.match(markup, /Company C/)
  assert.match(markup, /Seller A/)
  assert.match(markup, /Buyer B/)
  assert.match(markup, /Pending · T\+1 · due Day 8/)
  assert.match(markup, /Page 1 \/ 3/)
  assert.match(markup, /<button[^>]*disabled=""[^>]*>← Prev<\/button>/)
})

test('renders an honest empty state before the first fill', async (t) => {
  const markup = await renderTable(t, {
    transactions: [],
    total: 0,
    page: 1,
    pageSize: 20,
    participantNameById: new Map(),
    companyNameById: new Map(),
    onPage() {},
    onSelectCompany() {},
  })

  assert.match(markup, /No orders have been filled yet\./)
  assert.doesNotMatch(markup, /<table/)
})

test('labels a company-originated sell as Issuer', async (t) => {
  const markup = await renderTable(t, {
    transactions: [{
      id: 10,
      sellerId: null,
      buyerId: 20,
      companyId: 30,
      quantity: 25,
      price: 5,
      totalCost: 125,
      tradeDayNumber: 2,
      dueDayNumber: 3,
      settlementStatus: 'Pending',
    }],
    total: 1,
    page: 1,
    pageSize: 20,
    participantNameById: new Map([[20, 'Buyer B']]),
    companyNameById: new Map([[30, 'Company C']]),
    onPage() {},
    onSelectCompany() {},
  })

  assert.match(markup, /Issuer/)
  assert.match(markup, /Buyer B/)
})
