import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

test('bank loans page derives its server page size from the viewport', async () => {
  const source = await readFile(new URL('./BankLoansPage.jsx', import.meta.url), 'utf8')

  assert.match(source, /useFitPageSize/)
  assert.match(source, /const \[pageSize, tableRef\] = useFitPageSize\(\)/)
  assert.match(source, /pageSize,/)
  assert.match(source, /<div ref=\{tableRef\}>\s*<BankLoansTable/)
  assert.doesNotMatch(source, /const PAGE_SIZE =/)
})
