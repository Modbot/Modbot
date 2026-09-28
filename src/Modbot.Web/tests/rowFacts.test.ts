import assert from 'node:assert/strict'
import { test } from 'node:test'
import { keptFacts } from '../src/lib/rowFacts.ts'

test('a fact a row does not have leaves no gap on the second line', () => {
  assert.deepEqual(keptFacts([null, 'Trusted', undefined, false, '', 'seen 2 h ago']), ['Trusted', 'seen 2 h ago'])
})

test('zero is a fact, not a missing one', () => {
  assert.deepEqual(keptFacts([0, '0 visitors']), [0, '0 visitors'])
})

test('facts keep the order the columns have', () => {
  assert.deepEqual(keptFacts(['Manual', 'Sep 27, 2:57 AM']), ['Manual', 'Sep 27, 2:57 AM'])
})

test('a row with nothing to add has an empty second line', () => {
  assert.deepEqual(keptFacts([null, false]), [])
})
