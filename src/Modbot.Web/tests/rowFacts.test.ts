import assert from 'node:assert/strict'
import { test } from 'node:test'
import { caseFileFact, countsByKind, keptFacts } from '../src/lib/rowFacts.ts'

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

test('a ban with one case file says so in the singular', () => {
  assert.equal(caseFileFact({ caseId: 'c1', count: 1 }), '1 case file')
})

test('a ban with several case files counts them', () => {
  assert.equal(caseFileFact({ caseId: 'c1', count: 3 }), '3 case files')
})

test('a ban with no case file, or only a withdrawn one, adds no fact', () => {
  assert.equal(caseFileFact({ caseId: null, count: 0 }), null)
  assert.equal(caseFileFact({ caseId: null, count: 1 }), null)
})

test('a ban whose lookup has not come back yet adds no fact', () => {
  assert.equal(caseFileFact(undefined), null)
})

test("a moderator's counts follow the table's columns, with a kind never done as 0", () => {
  const kinds = [
    { metric: 'kicks', label: 'Instance kicks' },
    { metric: 'warns', label: 'Warns' },
    { metric: 'bans', label: 'Bans' },
  ]
  assert.deepEqual(countsByKind(kinds, { bans: 2, kicks: 1 }), [
    { label: 'Instance kicks', count: 1 },
    { label: 'Warns', count: 0 },
    { label: 'Bans', count: 2 },
  ])
})
