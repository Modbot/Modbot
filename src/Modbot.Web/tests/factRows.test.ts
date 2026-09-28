import assert from 'node:assert/strict'
import { test } from 'node:test'
import { factDays, groupByDay, mergeSameFacts, sameFact } from '../src/lib/factRows.ts'
import { formatDay } from '../src/lib/format.ts'
import type { AuditEntry } from '../src/lib/api.ts'

// Local times, so the days are the viewer's own whatever zone the tests run in.
const at = (month: number, day: number, hour: number, minute = 0, second = 0, ms = 0) =>
  new Date(2026, month - 1, day, hour, minute, second, ms).toISOString()

const NOW = at(9, 27, 21)

let nextId = 1

const fact = (extra: Partial<AuditEntry> = {}): AuditEntry => ({
  id: nextId++,
  occurredAt: at(9, 27, 7, 52, 4, 818),
  occurredBefore: null,
  observedAt: at(9, 27, 13, 20),
  precision: 'Exact',
  type: 'vrchat.group.member.join',
  typeRaw: null,
  category: 'Moderation',
  source: 'AuditLog',
  subjectPlatform: 'VRChat',
  subjectId: 'usr_fenya',
  subjectKind: 'Person',
  subjectName: 'FenyaKrautCutie',
  actorPlatform: null,
  actorId: null,
  actorName: null,
  subjectTrustRank: null,
  actorTrustRank: null,
  worldId: null,
  worldName: null,
  instanceId: null,
  modbotInstanceId: null,
  instanceName: null,
  description: null,
  data: null,
  ...extra,
})

// The pair seen on the test group: VRChat's audit log and the member list sync, 5 ms apart.
const vrchatJoin = () =>
  fact({ source: 'AuditLog', actorPlatform: 'VRChat', actorId: 'usr_fenya', occurredAt: at(9, 27, 7, 52, 4, 818) })
const syncJoin = () => fact({ source: 'SyncDiff', occurredAt: at(9, 27, 7, 52, 4, 813) })

test('one join recorded by VRChat and by the sync is one row with both', () => {
  const vrchat = vrchatJoin()
  const sync = syncJoin()

  const rows = mergeSameFacts([vrchat, sync])

  assert.equal(rows.length, 1)
  assert.equal(rows[0].entry, vrchat)
  assert.deepEqual(rows[0].also, [sync])
})

test("VRChat's copy is the one shown, whichever came first", () => {
  const vrchat = vrchatJoin()
  const sync = syncJoin()

  const rows = mergeSameFacts([sync, vrchat])

  assert.equal(rows[0].entry, vrchat)
  assert.deepEqual(rows[0].also, [sync])
})

test('a list of only VRChat rows or only sync rows shows each row as it is', () => {
  // What the audit log sends back when its source filter is set to one of the two.
  const vrchat = vrchatJoin()
  const sync = syncJoin()

  assert.deepEqual(mergeSameFacts([vrchat]), [{ entry: vrchat, also: [] }])
  assert.deepEqual(mergeSameFacts([sync]), [{ entry: sync, also: [] }])
})

test('two rows from one source are two things that happened', () => {
  const first = fact({ source: 'SyncDiff' })
  const second = fact({ source: 'SyncDiff' })

  assert.equal(mergeSameFacts([first, second]).length, 2)
})

test('a row takes one entry per source', () => {
  const vrchat = vrchatJoin()
  const syncA = syncJoin()
  const syncB = syncJoin()

  const rows = mergeSameFacts([vrchat, syncA, syncB])

  assert.equal(rows.length, 2)
  assert.deepEqual(rows[0].also, [syncA])
  assert.deepEqual(rows[1], { entry: syncB, also: [] })
})

test('different kinds, people, moderators or instances are not the same fact', () => {
  const vrchat = vrchatJoin()

  assert.equal(sameFact(vrchat, fact({ source: 'SyncDiff', type: 'vrchat.group.member.leave' })), false)
  assert.equal(sameFact(vrchat, fact({ source: 'SyncDiff', subjectId: 'usr_other' })), false)
  assert.equal(sameFact(vrchat, fact({ source: 'SyncDiff', subjectPlatform: 'Discord' })), false)
  assert.equal(sameFact(vrchat, fact({ source: 'Client', actorPlatform: 'VRChat', actorId: 'usr_mod' })), false)
  assert.equal(
    sameFact(fact({ instanceId: 'wrld_a:1' }), fact({ source: 'Client', instanceId: 'wrld_a:2' })),
    false,
  )
})

test('a missing moderator does not stop a merge, because the sync never knows one', () => {
  assert.equal(sameFact(vrchatJoin(), syncJoin()), true)
})

test('ten seconds apart is the same fact and eleven is not', () => {
  const vrchat = fact({ occurredAt: at(9, 27, 7, 52, 0) })

  assert.equal(sameFact(vrchat, fact({ source: 'SyncDiff', occurredAt: at(9, 27, 7, 52, 10) })), true)
  assert.equal(sameFact(vrchat, fact({ source: 'SyncDiff', occurredAt: at(9, 27, 7, 52, 11) })), false)
})

test('a fact known only to a window never merges', () => {
  const windowed = fact({
    source: 'SyncDiff',
    precision: 'Window',
    occurredAt: at(9, 27, 7, 50),
    occurredBefore: at(9, 27, 7, 55),
  })

  assert.equal(sameFact(vrchatJoin(), windowed), false)
})

test('rows sit under Today, Yesterday and the date, newest first', () => {
  const today = fact({ occurredAt: at(9, 27, 3, 11) })
  const yesterday = fact({ occurredAt: at(9, 26, 23, 59) })
  const earlier = fact({ occurredAt: at(9, 25, 11, 9) })
  const earlierToo = fact({ occurredAt: at(9, 25, 11, 9), type: 'vrchat.user.age-verified' })

  const days = factDays([today, yesterday, earlier, earlierToo], NOW)

  assert.deepEqual(
    days.map((d) => d.heading),
    ['Today', 'Yesterday', formatDay(earlier.occurredAt, false)],
  )
  assert.deepEqual(
    days.map((d) => d.rows.map((r) => r.entry)),
    [[today], [yesterday], [earlier, earlierToo]],
  )
})

test('midnight starts a new day', () => {
  const justAfter = fact({ occurredAt: at(9, 27, 0, 0, 1) })
  const justBefore = fact({ occurredAt: at(9, 26, 23, 59, 59) })

  const days = groupByDay(mergeSameFacts([justAfter, justBefore]), NOW)

  assert.deepEqual(
    days.map((d) => d.heading),
    ['Today', 'Yesterday'],
  )
})

test('"Today" is the server\'s day, not the one the rows are from', () => {
  const row = fact({ occurredAt: at(9, 27, 7) })

  assert.equal(factDays([row], at(9, 28, 9))[0].heading, 'Yesterday')
})

test('a day from another year says the year', () => {
  const lastYear = fact({ occurredAt: new Date(2025, 11, 31, 12).toISOString() })

  assert.match(factDays([lastYear], NOW)[0].heading, /2025/)
})

test('a merged pair sits under the day of the row that is shown', () => {
  const days = factDays([vrchatJoin(), syncJoin(), fact({ occurredAt: at(9, 25, 11, 9) })], NOW)

  assert.equal(days.length, 2)
  assert.equal(days[0].heading, 'Today')
  assert.equal(days[0].rows.length, 1)
  assert.equal(days[0].rows[0].also.length, 1)
})

test('an empty list has no days', () => {
  assert.deepEqual(factDays([], NOW), [])
})
