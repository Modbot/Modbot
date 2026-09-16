import assert from 'node:assert/strict'
import { test } from 'node:test'
import { textList } from '../src/lib/caseSnapshot.ts'

// The snapshot on a case file is free-form JSON handed back as it was written, so the page has to
// survive a field that is missing, null, or not a list. Reading `.length` off one of these blanked
// the whole case file page.
test('a missing or unusable list reads as no words', () => {
  assert.deepEqual(textList(undefined), [])
  assert.deepEqual(textList(null), [])
  assert.deepEqual(textList('system_trust_veteran'), [])
  assert.deepEqual(textList(7), [])
  assert.deepEqual(textList({ 0: 'a', length: 1 }), [])
})

test('a list of words reads as itself', () => {
  assert.deepEqual(textList([]), [])
  assert.deepEqual(textList(['Member', 'Event host']), ['Member', 'Event host'])
})

test('anything in the list that is not a word is dropped', () => {
  assert.deepEqual(textList(['Member', null, 3, { id: 'x' }, 'Admin']), ['Member', 'Admin'])
})
