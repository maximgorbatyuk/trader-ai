import assert from 'node:assert/strict'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'

async function loadModule(t) {
  const server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { middlewareMode: true },
  })
  t.after(() => server.close())

  try {
    const [components, model] = await Promise.all([
      server.ssrLoadModule('/src/PlayerOnboarding.jsx'),
      server.ssrLoadModule('/src/playerOnboardingModel.js'),
    ])
    return { ...components, ...model }
  } catch {
    return {}
  }
}

test('starts with an unskippable game introduction and five concise steps', async (t) => {
  const { PlayerOnboarding, PLAYER_ONBOARDING_STEPS } = await loadModule(t)
  assert.equal(typeof PlayerOnboarding, 'function')
  assert.equal(PLAYER_ONBOARDING_STEPS?.length, 5)

  const allCopy = PLAYER_ONBOARDING_STEPS.map((step) => `${step.title} ${step.description} ${step.points.join(' ')}`).join(' ')
  assert.match(allCopy, /managed fund/i)
  assert.match(allCopy, /AI trader/i)

  const markup = renderToStaticMarkup(createElement(PlayerOnboarding, { onCreated() {} }))
  assert.match(markup, /Step 1 of 5/)
  assert.match(markup, /This is a game/)
  assert.match(markup, /data-dismissible="false"/)
  assert.match(markup, />Continue</)
  assert.doesNotMatch(markup, />Skip</)
  assert.doesNotMatch(markup, />Close</)
})

test('the final step renders the player creation form', async (t) => {
  const { PlayerCreationForm } = await loadModule(t)
  assert.equal(typeof PlayerCreationForm, 'function')

  const markup = renderToStaticMarkup(createElement(PlayerCreationForm, {
    name: '',
    submitting: false,
    error: null,
    onNameChange() {},
    onSubmit() {},
    onBack() {},
  }))

  assert.match(markup, /<form/)
  assert.match(markup, />Name</)
  assert.match(markup, /placeholder="Player"/)
  assert.match(markup, />Create player</)
  assert.match(markup, />Back</)
})
