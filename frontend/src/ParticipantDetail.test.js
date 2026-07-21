import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'

async function loadModule(t, path) {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())
  return server.ssrLoadModule(path)
}

test('offers automation settings from actions for eligible traders', async (t) => {
  const { ParticipantActionsMenu } = await loadModule(t, '/src/ParticipantActions.jsx')
  assert.equal(typeof ParticipantActionsMenu, 'function')

  const individualMarkup = renderToStaticMarkup(
    createElement(ParticipantActionsMenu, {
      participant: { id: 1, type: 'Individual' },
      onChoose: () => {},
    }),
  )
  const fundMarkup = renderToStaticMarkup(
    createElement(ParticipantActionsMenu, {
      participant: { id: 2, type: 'CollectiveFund' },
      onChoose: () => {},
    }),
  )

  assert.match(individualMarkup, />Automation settings<\/button>/)
  assert.doesNotMatch(fundMarkup, /Automation settings/)
})

test('keeps AI call history in the Automation tab and explains when it is unavailable', async (t) => {
  const { AutomationTabContent } = await loadModule(t, '/src/ParticipantDetail.jsx')
  assert.equal(typeof AutomationTabContent, 'function')

  const individualMarkup = renderToStaticMarkup(
    createElement(AutomationTabContent, {
      participantId: 1,
      detail: { type: 'Individual' },
    }),
  )
  const aiAgentMarkup = renderToStaticMarkup(
    createElement(AutomationTabContent, {
      participantId: 2,
      detail: { type: 'AIAgent' },
    }),
  )

  assert.match(individualMarkup, />The trader is not AI managed<\/p>/)
  assert.doesNotMatch(aiAgentMarkup, /The trader is not AI managed/)
  assert.match(aiAgentMarkup, /AI calls/)
})
