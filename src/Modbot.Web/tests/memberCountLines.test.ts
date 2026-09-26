import assert from 'node:assert/strict'
import { test } from 'node:test'
import { BOTH_LINES, recallLines, rememberLines, switchLine } from '../src/pages/analytics/memberCountLines.ts'

test('either line can be hidden while the other shows', () => {
  assert.deepEqual(switchLine(BOTH_LINES, 'members', false), { members: false, online: true })
  assert.deepEqual(switchLine(BOTH_LINES, 'online', false), { members: true, online: false })
})

test('the last line showing cannot be hidden', () => {
  const onlineOnly = { members: false, online: true }
  assert.deepEqual(switchLine(onlineOnly, 'online', false), onlineOnly)
})

test('a hidden line can be shown again', () => {
  assert.deepEqual(switchLine({ members: false, online: true }, 'members', true), BOTH_LINES)
})

// Under node the store is missing or empty, like a private window or blocked site data.
test('with nothing stored, both lines show, and remembering never throws', () => {
  assert.deepEqual(recallLines(), BOTH_LINES)
  assert.doesNotThrow(() => rememberLines({ members: false, online: true }))
})
