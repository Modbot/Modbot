import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  CHORD_TIMEOUT_MS,
  describeKeys,
  hasPageActions,
  isTyping,
  keyToken,
  matchKeys,
  onKeyDown,
  registerShortcut,
  waitingChord,
} from '../src/lib/shortcuts.ts'

test('a page has actions only when it registered a key of its own that the sheet lists', () => {
  // Now: the palette, the sheet and the go-to keys, all the app's own.
  assert.equal(
    hasPageActions([
      { group: 'General', hidden: false },
      { group: 'Go to', hidden: false },
    ]),
    false,
  )
  // A key the sheet leaves out does not count: there would be nothing to tap.
  assert.equal(hasPageActions([{ group: 'General' }, { group: 'Lists', hidden: true }]), false)
  assert.equal(hasPageActions([{ group: 'General' }, { group: 'Lists' }]), true)
  assert.equal(hasPageActions([{ group: 'Calendar' }]), true)
  assert.equal(hasPageActions([]), false)
})

test('a letter typed into a text box is typing; a checkbox is not', () => {
  assert.equal(isTyping({ tagName: 'INPUT' }), true)
  assert.equal(isTyping({ tagName: 'input', type: 'search' }), true)
  assert.equal(isTyping({ tagName: 'TEXTAREA' }), true)
  assert.equal(isTyping({ tagName: 'SELECT' }), true)
  assert.equal(isTyping({ tagName: 'DIV', isContentEditable: true }), true)
  assert.equal(isTyping({ tagName: 'INPUT', type: 'checkbox' }), false)
  assert.equal(isTyping({ tagName: 'BUTTON' }), false)
  assert.equal(isTyping({ tagName: 'TR' }), false)
  assert.equal(isTyping(null), false)
})

test('a key press becomes a token with modifiers first', () => {
  assert.equal(keyToken({ key: 'k', ctrlKey: true }), 'mod+k')
  assert.equal(keyToken({ key: 'k', metaKey: true }), 'mod+k')
  assert.equal(keyToken({ key: 'J' }), 'j')
  assert.equal(keyToken({ key: 'Escape' }), 'escape')
  assert.equal(keyToken({ key: 'ArrowDown' }), 'arrowdown')
  assert.equal(keyToken({ key: 'Enter', shiftKey: true }), 'shift+enter')
  assert.equal(keyToken({ key: 'Control' }), null)
})

test('shift is not written for a printable key, because ? already says it', () => {
  assert.equal(keyToken({ key: '?', shiftKey: true }), '?')
  assert.equal(keyToken({ key: '/', shiftKey: false }), '/')
})

test('g then m runs the chord; g alone waits', () => {
  const keys = ['g m', 'g a', 'j', 'mod+k']

  assert.deepEqual(matchKeys(keys, null, 'g'), { pending: 'g' })
  assert.deepEqual(matchKeys(keys, 'g', 'm'), { run: 'g m' })
  assert.deepEqual(matchKeys(keys, null, 'j'), { run: 'j' })
  assert.deepEqual(matchKeys(keys, null, 'mod+k'), { run: 'mod+k' })
})

test('a chord that goes nowhere falls back to the single key', () => {
  const keys = ['g m', 'j']

  assert.deepEqual(matchKeys(keys, 'g', 'j'), { run: 'j' })
  assert.deepEqual(matchKeys(keys, 'g', 'z'), {})
  assert.deepEqual(matchKeys(keys, null, 'z'), {})
})

test('keys are described the way a person reads them', () => {
  assert.equal(describeKeys('mod+k', false), 'Ctrl K')
  assert.equal(describeKeys('mod+k', true), '⌘ K')
  assert.equal(describeKeys('g m', false), 'G then M')
  assert.equal(describeKeys('escape', false), 'Esc')
  assert.equal(describeKeys('?', false), '?')
  assert.equal(describeKeys('shift+enter', false), 'Shift Enter')
})

test('the sidebar can tell a g is waiting, for exactly as long as the second key works', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] })
  const off = registerShortcut({ keys: 'g k', label: 'Now', group: 'Go to', run: () => {} })
  t.after(off)

  const press = (key: string) =>
    onKeyDown({
      key,
      ctrlKey: false,
      metaKey: false,
      altKey: false,
      shiftKey: false,
      target: null,
      defaultPrevented: false,
      isComposing: false,
      preventDefault: () => {},
    })

  // Pressed, then the second key: waiting, then not.
  press('g')
  assert.equal(waitingChord(), 'g')
  press('k')
  assert.equal(waitingChord(), null)

  // Pressed and left: the wait ends when the chord stops listening, not later.
  press('g')
  t.mock.timers.tick(CHORD_TIMEOUT_MS - 1)
  assert.equal(waitingChord(), 'g')
  t.mock.timers.tick(1)
  assert.equal(waitingChord(), null)

  // A letter that starts nothing does not wait.
  press('x')
  assert.equal(waitingChord(), null)
})
