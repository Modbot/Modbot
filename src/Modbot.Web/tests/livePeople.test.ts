import assert from 'node:assert/strict'
import { test } from 'node:test'
import { arrivedWithin, LIT_MS, NEW_MS, pinned, tallyCounts, watchFor } from '../src/lib/livePeople.ts'

/**
 * The Live page's rules for a glance: who is New, whose row is lit, who is pinned to the top, and
 * how the running line reads.
 */

const NOW = Date.parse('2026-09-27T21:00:00Z')
const minutesAgo = (m: number) => new Date(NOW - m * 60_000).toISOString()

test('somebody seen arriving under five minutes ago is New, and their row is lit for the first minute', () => {
  const justNow = { arrivedAt: minutesAgo(0.5) }
  const aWhile = { arrivedAt: minutesAgo(3) }

  assert.equal(arrivedWithin(justNow, NOW, NEW_MS), true)
  assert.equal(arrivedWithin(justNow, NOW, LIT_MS), true)
  assert.equal(arrivedWithin(aWhile, NOW, NEW_MS), true)
  assert.equal(arrivedWithin(aWhile, NOW, LIT_MS), false)
})

test('after five minutes, nobody is New', () => {
  assert.equal(arrivedWithin({ arrivedAt: minutesAgo(5) }, NOW, NEW_MS), false)
  assert.equal(arrivedWithin({ arrivedAt: minutesAgo(40) }, NOW, NEW_MS), false)
})

test('somebody already there when watching began is never New', () => {
  assert.equal(arrivedWithin({ arrivedAt: null }, NOW, NEW_MS), false)
})

test('a list that is not live marks nobody New', () => {
  assert.equal(arrivedWithin({ arrivedAt: minutesAgo(1) }, undefined, NEW_MS), false)
})

test('an arrival stamped after the server time it came with is not New', () => {
  assert.equal(arrivedWithin({ arrivedAt: minutesAgo(-2) }, NOW, NEW_MS), false)
})

test('flagged people and anybody kicked or banned before are watched for', () => {
  assert.equal(watchFor({ standing: 'Flagged', priorActions: 0 }), true)
  assert.equal(watchFor({ standing: 'Member', priorActions: 1 }), true)
  assert.equal(watchFor({ standing: 'Member', priorActions: 0 }), false)
  assert.equal(watchFor({ standing: 'Staff', priorActions: 0 }), false)
})

test('the people watched for go first, and everybody else keeps the order they came in', () => {
  const people = [
    { id: 'a', standing: 'Member', priorActions: 0 },
    { id: 'b', standing: 'Flagged', priorActions: 2 },
    { id: 'c', standing: 'Ordinary', priorActions: 0 },
    { id: 'd', standing: 'Member', priorActions: 1 },
    { id: 'e', standing: 'Staff', priorActions: 0 },
  ]

  assert.deepEqual(
    pinned(people).map((p) => p.id),
    ['b', 'd', 'a', 'c', 'e'],
  )
})

test('pinning leaves the list it was given alone', () => {
  const people = [
    { id: 'a', standing: 'Member', priorActions: 0 },
    { id: 'b', standing: 'Flagged', priorActions: 0 },
  ]

  pinned(people)
  assert.deepEqual(
    people.map((p) => p.id),
    ['a', 'b'],
  )
})

test('the running line says one arrival, two kicks', () => {
  assert.deepEqual(tallyCounts({ since: minutesAgo(60), arrivals: 1, warns: 0, kicks: 2, bans: 1 }), [
    [1, 'arrival'],
    [0, 'warns'],
    [2, 'kicks'],
    [1, 'ban'],
  ])
})
