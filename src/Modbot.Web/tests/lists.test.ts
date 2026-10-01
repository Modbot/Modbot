import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { CurrentUser } from '../src/lib/api.ts'
import { mayOpen } from '../src/lib/nav.ts'
import {
  fromPolledData,
  offeredKinds,
  takesAmount,
  takesDate,
  takesList,
  takesModerationKind,
  takesWindow,
} from '../src/lib/giveawayRules.ts'
import { countWords, fileNameFrom, peopleWords } from '../src/lib/listWords.ts'

function person(...permissionNames: string[]): CurrentUser {
  return { permissionNames } as CurrentUser
}

test('Lists needs See members and See profiles both, and making one is not enough', () => {
  assert.equal(mayOpen(person(), 'lists'), false)
  assert.equal(mayOpen(person('ManageLists'), 'lists'), false)
  assert.equal(mayOpen(person('ViewMembers'), 'lists'), false)
  assert.equal(mayOpen(person('ViewProfile'), 'lists'), false)
  assert.equal(mayOpen(person('ViewMembers', 'ViewProfile'), 'lists'), true)
  assert.equal(mayOpen(person('Administrator'), 'lists'), true)
})

test('a count from presence reports says about, and an exact one does not', () => {
  assert.equal(countWords({ count: 312, fromPolledData: true }), 'about 312')
  assert.equal(countWords({ count: 312, fromPolledData: false }), '312')
  assert.equal(peopleWords({ count: 1, fromPolledData: false }), '1 person')
  assert.equal(peopleWords({ count: 2, fromPolledData: true }), 'about 2 people')
})

test('the file name comes from the server, and the fallback stands in when it names none', () => {
  assert.equal(fileNameFrom('attachment; filename=regulars-2026-10-01.csv; filename*=UTF-8\'\'regulars-2026-10-01.csv', 'list.csv'), 'regulars-2026-10-01.csv')
  assert.equal(fileNameFrom('attachment; filename="regulars.json"', 'list.json'), 'regulars.json')
  assert.equal(fileNameFrom(null, 'list.csv'), 'list.csv')
})

test('the new rules take what the server reads for them', () => {
  for (const kind of ['groupJoinedWithinDays', 'firstSeenWithinDays', 'daysSeen', 'notSeenWithinDays', 'moderationCount'])
    assert.equal(takesAmount(kind), true, kind)

  assert.equal(takesWindow('daysSeen'), true)
  assert.equal(takesWindow('moderationCount'), true)
  assert.equal(takesWindow('notSeenWithinDays'), false)
  assert.equal(takesDate('groupJoinedBefore'), true)
  assert.equal(takesDate('groupJoinedSince'), true)
  assert.equal(takesAmount('groupJoinedBefore'), false)
  assert.equal(takesList('inList'), true)
  assert.equal(takesModerationKind('moderationCount'), true)
  assert.equal(fromPolledData('daysSeen'), true)
  assert.equal(fromPolledData('notSeenWithinDays'), true)
  assert.equal(fromPolledData('moderationCount'), false)
})

test('"In the list" is offered only where there is a list to pick', () => {
  const kinds = ['inGroup', 'inList']
  assert.deepEqual(offeredKinds(kinds, undefined), ['inGroup'])
  assert.deepEqual(offeredKinds(kinds, []), ['inGroup'])
  assert.deepEqual(offeredKinds(kinds, [{ id: 'a' }]), ['inGroup', 'inList'])
})
