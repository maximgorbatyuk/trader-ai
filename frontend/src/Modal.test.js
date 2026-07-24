import assert from 'node:assert/strict'
import test from 'node:test'
import { trapModalFocus } from './modalFocus.js'

function focusTarget() {
  return {
    focusCount: 0,
    focus() {
      this.focusCount += 1
    },
  }
}

function tabEvent(shiftKey = false) {
  return {
    key: 'Tab',
    shiftKey,
    prevented: false,
    preventDefault() {
      this.prevented = true
    },
  }
}

test('Shift+Tab from the initially focused dialog wraps to the last control', () => {
  const first = focusTarget()
  const last = focusTarget()
  const dialog = {
    ...focusTarget(),
    querySelectorAll() {
      return [first, last]
    },
  }
  const event = tabEvent(true)

  trapModalFocus(event, dialog, dialog)

  assert.equal(event.prevented, true)
  assert.equal(last.focusCount, 1)
  assert.equal(first.focusCount, 0)
})

test('Tab from the initially focused dialog moves to the first control', () => {
  const first = focusTarget()
  const last = focusTarget()
  const dialog = {
    ...focusTarget(),
    querySelectorAll() {
      return [first, last]
    },
  }
  const event = tabEvent()

  trapModalFocus(event, dialog, dialog)

  assert.equal(event.prevented, true)
  assert.equal(first.focusCount, 1)
  assert.equal(last.focusCount, 0)
})

test('Tab stays on the dialog when it has no focusable controls', () => {
  const dialog = {
    ...focusTarget(),
    querySelectorAll() {
      return []
    },
  }
  const event = tabEvent()

  trapModalFocus(event, dialog, dialog)

  assert.equal(event.prevented, true)
  assert.equal(dialog.focusCount, 1)
})
