import assert from 'node:assert/strict'
import { test } from 'node:test'
import { visibleArea } from '../src/lib/visibleArea.ts'

test('with no keyboard up, the whole page is in sight', () => {
  assert.deepEqual(visibleArea(844, { height: 844, offsetTop: 0, scale: 1 }), { height: 844, top: 0, keyboard: 0 })
})

test('on iOS the keyboard shrinks only the part in sight, and covers the rest of the page', () => {
  assert.deepEqual(visibleArea(844, { height: 508, offsetTop: 0, scale: 1 }), { height: 508, top: 0, keyboard: 336 })
})

test('when iOS scrolls the part in sight down the page, the keyboard covers only what is below it', () => {
  assert.deepEqual(visibleArea(844, { height: 508, offsetTop: 120, scale: 1 }), { height: 508, top: 120, keyboard: 216 })
})

test('on Android the page itself shrinks above the keyboard, so the keyboard covers none of it', () => {
  // interactive-widget=resizes-content: the page and the part in sight are the same height.
  assert.deepEqual(visibleArea(508, { height: 508, offsetTop: 0, scale: 1 }), { height: 508, top: 0, keyboard: 0 })
})

test('a page zoomed in with two fingers is not taken for a keyboard', () => {
  assert.deepEqual(visibleArea(844, { height: 422, offsetTop: 200, scale: 2 }), { height: 844, top: 0, keyboard: 0 })
})

test('a part in sight reported taller than the page is held to the page', () => {
  assert.deepEqual(visibleArea(844, { height: 850, offsetTop: 0, scale: 1 }), { height: 844, top: 0, keyboard: 0 })
})
