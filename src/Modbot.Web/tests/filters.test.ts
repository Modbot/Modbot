import assert from 'node:assert/strict'
import { test } from 'node:test'
import { dateRange, decodeChip, encodeChip, readChips, sameChips, writeChips, type FilterChip } from '../src/lib/filters.ts'
import {
  AUDIT_DEFAULTS,
  auditQueryFrom,
  discordMemberQueryFrom,
  memberQueryFrom,
  PEOPLE_DEFAULTS,
  peopleQueryFrom,
} from '../src/lib/pageFilters.ts'

test('a chip survives the trip through the address, commas in values included', () => {
  const chip: FilterChip = { property: 'subject', operator: 'is', values: ['weird,id:with/stuff %'] }

  assert.deepEqual(decodeChip(encodeChip(chip)), chip)

  const params = new URLSearchParams()
  writeChips(params, [chip, { property: 'source', operator: 'is-not', values: ['SyncDiff', 'Manual'] }])
  assert.deepEqual(readChips(new URLSearchParams(params.toString())), [
    chip,
    { property: 'source', operator: 'is-not', values: ['SyncDiff', 'Manual'] },
  ])
})

test('no filters at all is written so it can be told apart from nothing said', () => {
  const params = new URLSearchParams('subject=usr_a')
  writeChips(params, [])

  assert.deepEqual(readChips(params), [])
  assert.equal(readChips(new URLSearchParams('subject=usr_a')), null)
  assert.equal(params.get('subject'), 'usr_a')
})

test('a chip that cannot be read is dropped rather than breaking the page', () => {
  assert.equal(decodeChip('nonsense'), null)
  assert.deepEqual(readChips(new URLSearchParams('f=nonsense&f=source:is:Client')), [
    { property: 'source', operator: 'is', values: ['Client'] },
  ])
})

test('chips compare regardless of order', () => {
  const a: FilterChip[] = [
    { property: 'source', operator: 'is', values: ['Client', 'AuditLog'] },
    { property: 'hasActor', operator: 'yes', values: [] },
  ]
  const b: FilterChip[] = [
    { property: 'hasActor', operator: 'yes', values: [] },
    { property: 'source', operator: 'is', values: ['AuditLog', 'Client'] },
  ]

  assert.equal(sameChips(a, b), true)
  assert.equal(sameChips(a, [b[0]]), false)
})

test('a date chip becomes a half-open stretch that includes the last day', () => {
  const between = dateRange([{ property: 'when', operator: 'between', values: ['2026-03-10', '2026-03-12'] }], 'when')
  assert.deepEqual(between, { from: '2026-03-10T00:00:00Z', to: '2026-03-13T00:00:00.000Z' })

  const after = dateRange([{ property: 'when', operator: 'after', values: ['2026-03-10'] }], 'when')
  assert.deepEqual(after, { from: '2026-03-10T00:00:00Z' })

  const before = dateRange([{ property: 'when', operator: 'before', values: ['2026-03-10'] }], 'when')
  assert.deepEqual(before, { to: '2026-03-10T00:00:00Z' })
})

test('the audit log leaves Sync out by default and turns is-not into the rest of the list', () => {
  assert.deepEqual(auditQueryFrom(AUDIT_DEFAULTS).source, ['AuditLog', 'Discord', 'Client', 'Import'])

  const query = auditQueryFrom([
    { property: 'source', operator: 'is-not', values: ['SyncDiff'] },
    { property: 'actor', operator: 'is', values: ['usr_mod'] },
    { property: 'hasActor', operator: 'no', values: [] },
    { property: 'text', operator: 'contains', values: ['black cat'] },
  ])

  assert.deepEqual(query.source, ['AuditLog', 'Client', 'Discord', 'Manual', 'Modbot', 'Import'])
  assert.equal(query.actor, 'usr_mod')
  assert.equal(query.hasActor, false)
  assert.equal(query.q, 'black cat')
  assert.equal(query.type, undefined)
})

test('a role chip is any-of or none-of, and status defaults to everybody once its chip is gone', () => {
  const any = memberQueryFrom([{ property: 'role', operator: 'is', values: ['grol_a', 'grol_b'] }])
  assert.deepEqual(any.roles, ['grol_a', 'grol_b'])
  assert.equal(any.notRoles, undefined)
  assert.equal(any.status, 'all')

  const none = memberQueryFrom([
    { property: 'role', operator: 'is-not', values: ['grol_a'] },
    { property: 'status', operator: 'is', values: ['current'] },
    { property: 'eighteenPlus', operator: 'yes', values: [] },
  ])
  assert.deepEqual(none.notRoles, ['grol_a'])
  assert.equal(none.status, 'current')
  assert.equal(none.eighteenPlus, true)
})

test('the Discord list reads its yes/no chips', () => {
  const query = discordMemberQueryFrom([
    { property: 'bot', operator: 'no', values: [] },
    { property: 'timedOut', operator: 'yes', values: [] },
    { property: 'state', operator: 'is', values: ['left'] },
  ])

  assert.equal(query.bot, false)
  assert.equal(query.timedOut, true)
  assert.equal(query.state, 'left')
  assert.equal(query.pending, undefined)
})

test('People opens on the whole record and every chip narrows it', () => {
  const nothing = peopleQueryFrom(PEOPLE_DEFAULTS)
  assert.equal(nothing.membership, 'all')
  assert.equal(nothing.banned, undefined)
  assert.equal(nothing.everBanned, undefined)
  assert.equal(nothing.eighteenPlus, undefined)
  assert.equal(nothing.trustRanks, undefined)
  assert.equal(nothing.platforms, undefined)
  assert.equal(nothing.linked, undefined)
  assert.equal(nothing.flagged, undefined)
  assert.equal(nothing.profile, undefined)
  assert.equal(nothing.seenFrom, undefined)

  const query = peopleQueryFrom([
    { property: 'membership', operator: 'is', values: ['not-member'] },
    { property: 'banned', operator: 'no', values: [] },
    { property: 'everBanned', operator: 'yes', values: [] },
    { property: 'trustRank', operator: 'is', values: ['Visitor', 'NewUser'] },
    { property: 'platform', operator: 'is', values: ['android'] },
    { property: 'linked', operator: 'is', values: ['not-linked'] },
    { property: 'eighteenPlus', operator: 'no', values: [] },
    { property: 'flagged', operator: 'yes', values: [] },
    { property: 'profile', operator: 'is', values: ['not-fetched'] },
    { property: 'seen', operator: 'after', values: ['2026-03-10'] },
  ])

  assert.equal(query.membership, 'not-member')
  assert.equal(query.banned, false)
  assert.equal(query.everBanned, true)
  assert.deepEqual(query.trustRanks, ['Visitor', 'NewUser'])
  assert.deepEqual(query.platforms, ['android'])
  assert.equal(query.linked, 'not-linked')
  assert.equal(query.eighteenPlus, false)
  assert.equal(query.flagged, true)
  assert.equal(query.profile, 'not-fetched')
  assert.equal(query.seenFrom, '2026-03-10T00:00:00Z')
  assert.equal(query.seenTo, undefined)
})

test('trust rank and platform ask only for what was picked, never for the rest of the list', () => {
  // Neither is negatable in the bar, and a chip that somehow says "is not" asks for nothing rather
  // than for every rank but one -- which would drop everybody whose tags have never been read.
  const negated = peopleQueryFrom([
    { property: 'trustRank', operator: 'is-not', values: ['Visitor'] },
    { property: 'platform', operator: 'is-not', values: ['android'] },
  ])

  assert.equal(negated.trustRanks, undefined)
  assert.equal(negated.platforms, undefined)

  const empty = peopleQueryFrom([{ property: 'trustRank', operator: 'is', values: [] }])
  assert.equal(empty.trustRanks, undefined)
})
