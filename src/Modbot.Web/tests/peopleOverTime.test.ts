import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { InstanceView, PeoplePresentPoint } from '../src/lib/api.ts'
import { peopleOverTimeRows, presenceLines, type PeopleRow, type StepKind } from '../src/lib/peopleOverTime.ts'

const T0 = Date.parse('2026-09-28T20:00:00Z')
const at = (minutes: number) => new Date(T0 + minutes * 60_000).toISOString()
const ms = (minutes: number) => T0 + minutes * 60_000

type Reading = InstanceView['headCounts'][number]

function reading(minutes: number, people: number, change: Reading['change'], unsure = false): Reading {
  return { at: at(minutes), people, userCount: people, memberCount: null, source: 'page', nUsers: null, unsure, change }
}

function present(minutes: number, counts: Partial<Omit<PeoplePresentPoint, 'at'>>): PeoplePresentPoint {
  return {
    at: at(minutes),
    members: 0,
    visitor: 0,
    newUser: 0,
    user: 0,
    knownUser: 0,
    trustedUser: 0,
    legend: 0,
    nuisance: 0,
    vrChatTeam: 0,
    rankUnknown: 0,
    ...counts,
  }
}

/** The steps one colour's line draws: each run of rows where its key has a value. */
function runs(rows: PeopleRow[], kind: StepKind): [number, number][][] {
  const out: [number, number][][] = []
  let run: [number, number][] = []
  for (const row of rows) {
    if (row[kind] === null) {
      if (run.length > 0) out.push(run)
      run = []
    } else run.push([row.at, row[kind]])
  }
  if (run.length > 0) out.push(run)
  return out
}

// ── The coloured staircase ─────────────────────────────────────────────────────────────────────

test('a step is coloured by the reading it ends in, and the tail after the last reading is held', () => {
  const rows = peopleOverTimeRows(
    [reading(0, 4, null), reading(10, 7, 'up'), reading(20, 5, 'kick'), reading(30, 4, 'left')],
    [],
    ms(40),
  )

  assert.deepEqual(runs(rows, 'up'), [[[ms(0), 4], [ms(10), 7]]])
  assert.deepEqual(runs(rows, 'kick'), [[[ms(10), 7], [ms(20), 5]]])
  assert.deepEqual(runs(rows, 'left'), [[[ms(20), 5], [ms(30), 4]]])
  assert.deepEqual(runs(rows, 'held'), [[[ms(30), 4], [ms(40), 4]]])
})

test('two runs of one colour with another between them are two runs, not one line through the other', () => {
  const rows = peopleOverTimeRows(
    [reading(0, 4, null), reading(10, 3, 'kick'), reading(20, 6, 'up'), reading(30, 5, 'kick')],
    [],
    ms(30),
  )

  assert.deepEqual(runs(rows, 'kick'), [
    [[ms(0), 4], [ms(10), 3]],
    [[ms(20), 6], [ms(30), 5]],
  ])
  assert.deepEqual(runs(rows, 'up'), [[[ms(10), 3], [ms(20), 6]]])
  assert.deepEqual(runs(rows, 'held'), [])
})

test('a reading that only changed source is a held step', () => {
  const rows = peopleOverTimeRows([reading(0, 4, null), reading(10, 4, null)], [], ms(10))

  assert.deepEqual(runs(rows, 'held'), [[[ms(0), 4], [ms(10), 4]]])
})

test('every row says what the head count was at its instant, and no head counts means no rows', () => {
  const rows = peopleOverTimeRows([reading(0, 4, null), reading(10, 7, 'up')], [], ms(20))

  assert.deepEqual(
    rows.map((r) => [r.at, r.people, r.change]),
    [
      [ms(0), 4, null],
      [ms(10), 7, 'up'],
      [ms(10), 7, 'up'],
      [ms(20), 7, 'up'],
    ],
  )
  assert.deepEqual(peopleOverTimeRows([], [present(5, { members: 2 })], ms(20)), [])
})

test('an unsure reading stays unsure on its rows', () => {
  const rows = peopleOverTimeRows([reading(0, 80, null, true), reading(10, 51, 'left')], [], ms(10))

  assert.deepEqual(
    rows.map((r) => [r.people, r.unsure]),
    [
      [80, true],
      [51, false],
    ],
  )
})

// ── The lines a companion adds ────────────────────────────────────────────────────────────────

test('a presence change inside a step keeps the step on its line and carries the head count', () => {
  const rows = peopleOverTimeRows(
    [reading(0, 4, null), reading(20, 7, 'up')],
    [present(5, { members: 2, user: 2 }), present(15, { members: 1, user: 1 }), present(20, {})],
    ms(20),
  )

  assert.deepEqual(runs(rows, 'up'), [[[ms(0), 4], [ms(5), 4], [ms(15), 4], [ms(20), 7], [ms(20), 7]]])
  assert.deepEqual(
    rows.map((r) => [r.at, r.people, r.members]),
    [
      [ms(0), 4, null],
      [ms(5), 4, 2],
      [ms(15), 4, 1],
      [ms(20), 7, 0],
      [ms(20), 7, 0],
    ],
  )
})

test('a line has no value before its first point or after its last, and its latest value between', () => {
  const rows = peopleOverTimeRows(
    [reading(0, 4, null), reading(10, 5, 'up'), reading(40, 6, 'up')],
    [present(20, { members: 3, trustedUser: 1 }), present(30, {})],
    ms(50),
  )

  const members = rows.map((r) => [r.at, r.members])
  assert.deepEqual(members, [
    [ms(0), null],
    [ms(10), null],
    [ms(10), null],
    [ms(20), 3],
    [ms(30), 0],
    [ms(40), null],
    [ms(40), null],
    [ms(50), null],
  ])
  assert.deepEqual(rows.find((r) => r.at === ms(20))?.trustedUser, 1)
})

test('rows at one instant agree, whichever the tooltip lands on', () => {
  const rows = peopleOverTimeRows(
    [reading(0, 4, null), reading(10, 7, 'up'), reading(20, 6, 'left')],
    [present(10, { members: 5 })],
    ms(20),
  )

  const atTen = rows.filter((r) => r.at === ms(10))
  assert.equal(atTen.length, 3)
  assert.deepEqual(new Set(atTen.map((r) => `${r.people}/${r.members}/${r.change}`)), new Set(['7/5/up']))
})

test('the everyday ranks always have a line; the rare ones only when somebody held them', () => {
  assert.deepEqual(presenceLines([present(0, { user: 2 })]), [
    'members',
    'visitor',
    'newUser',
    'user',
    'knownUser',
    'trustedUser',
  ])
  assert.deepEqual(presenceLines([present(0, { user: 2 }), present(5, { nuisance: 1, rankUnknown: 1 })]), [
    'members',
    'visitor',
    'newUser',
    'user',
    'knownUser',
    'trustedUser',
    'nuisance',
    'rankUnknown',
  ])
})
