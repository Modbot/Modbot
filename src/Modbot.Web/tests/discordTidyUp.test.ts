import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  CHANNEL_SORT_DEFAULT,
  NO_CATEGORY,
  ROLE_FLAG_LABELS,
  channelQueryFrom,
  channelSortParam,
  channelStatus,
  filterChannels,
  firstDirection,
  quietFor,
  readChannelSort,
  sortChannels,
} from '../src/lib/discordTidyUp.ts'
import type { QuietChannelRow } from '../src/lib/api.ts'
import type { FilterChip } from '../src/lib/filters.ts'

const NOW = '2026-10-02T12:00:00Z'

function channel(over: Partial<QuietChannelRow> = {}): QuietChannelRow {
  return {
    id: '1',
    name: 'general',
    type: 'text',
    categoryId: null,
    categoryName: null,
    staffOnly: false,
    canRead: true,
    stillReading: false,
    lastMessageAt: null,
    ...over,
  }
}

test('each mark on a role is named in plain words', () => {
  assert.deepEqual(ROLE_FLAG_LABELS, {
    'no-members': 'No members',
    'same-name': 'Same name as another role',
    'same-permissions-and-colour': 'Same permissions and colour as another role',
    'bot-role': 'Bot role',
  })
})

test('how long a channel has been quiet is in Modbot’s units, never weeks, against the server’s clock', () => {
  assert.equal(quietFor(channel({ lastMessageAt: '2026-10-02T11:55:00Z' }), NOW), '5m')
  assert.equal(quietFor(channel({ lastMessageAt: '2026-10-02T07:00:00Z' }), NOW), '5h')
  // Three weeks is days, not "3w".
  assert.equal(quietFor(channel({ lastMessageAt: '2026-09-11T12:00:00Z' }), NOW), '21d')
  assert.equal(quietFor(channel({ lastMessageAt: '2026-07-01T12:00:00Z' }), NOW), '3mth')
  assert.equal(quietFor(channel({ lastMessageAt: '2021-09-01T12:00:00Z' }), NOW), '5y')
})

test('with no last message, the channel says why', () => {
  assert.equal(quietFor(channel(), NOW), 'No messages')
  assert.equal(quietFor(channel({ stillReading: true }), NOW), 'Still reading')
  assert.equal(quietFor(channel({ canRead: false }), NOW), "Can't read")
  // A channel the bot cannot read says so even if a time came with it.
  assert.equal(quietFor(channel({ canRead: false, lastMessageAt: '2026-10-01T12:00:00Z' }), NOW), "Can't read")
})

// ── Sorting ─────────────────────────────────────────────────────────────────────────────────────

const ROWS = [
  channel({ id: 'recent', name: 'Beta', categoryId: 'c2', categoryName: 'Voice stuff', lastMessageAt: '2026-10-01T12:00:00Z' }),
  channel({ id: 'old', name: 'alpha', categoryId: 'c1', categoryName: 'Town', lastMessageAt: '2024-10-01T12:00:00Z' }),
  channel({ id: 'never', name: 'gamma-10' }),
  channel({ id: 'reading', name: 'gamma-9', categoryId: 'c1', categoryName: 'Town', stillReading: true }),
  channel({ id: 'locked', name: 'delta', canRead: false, staffOnly: true }),
]

const ids = (rows: QuietChannelRow[]) => rows.map((r) => r.id)

test('Quiet for sorts longest quiet first by default, with unknown times always last', () => {
  assert.deepEqual(ids(sortChannels(ROWS, CHANNEL_SORT_DEFAULT, NOW)), ['never', 'old', 'recent', 'reading', 'locked'])
  // Reversed: shortest first, nobody-wrote after the dated ones, but still-reading and cannot-read stay last.
  assert.deepEqual(ids(sortChannels(ROWS, { key: 'quiet', dir: 'asc' }, NOW)), ['recent', 'old', 'never', 'reading', 'locked'])
})

test('Last message sorts by the day, rows with none last either way', () => {
  assert.deepEqual(ids(sortChannels(ROWS, { key: 'last', dir: 'desc' }, NOW)), ['recent', 'old', 'never', 'reading', 'locked'])
  assert.deepEqual(ids(sortChannels(ROWS, { key: 'last', dir: 'asc' }, NOW)), ['old', 'recent', 'never', 'reading', 'locked'])
})

test('Channel sorts by name, ignoring case and counting numbers as numbers', () => {
  assert.deepEqual(ids(sortChannels(ROWS, { key: 'name', dir: 'asc' }, NOW)), ['old', 'recent', 'locked', 'reading', 'never'])
  assert.deepEqual(ids(sortChannels(ROWS, { key: 'name', dir: 'desc' }, NOW)), ['never', 'reading', 'locked', 'recent', 'old'])
})

test('Category sorts by name, channels in none last either way, ties keeping their order', () => {
  assert.deepEqual(ids(sortChannels(ROWS, { key: 'category', dir: 'asc' }, NOW)), ['old', 'reading', 'recent', 'never', 'locked'])
  assert.deepEqual(ids(sortChannels(ROWS, { key: 'category', dir: 'desc' }, NOW)), ['recent', 'old', 'reading', 'never', 'locked'])
})

test('sorting leaves the list it was given alone', () => {
  const before = ids(ROWS)
  sortChannels(ROWS, { key: 'name', dir: 'asc' }, NOW)
  assert.deepEqual(ids(ROWS), before)
})

test('the sort is kept in the address as one word, nothing for the default', () => {
  assert.equal(channelSortParam(CHANNEL_SORT_DEFAULT), null)
  assert.equal(channelSortParam({ key: 'last', dir: 'asc' }), 'last-asc')
  assert.deepEqual(readChannelSort('last-asc'), { key: 'last', dir: 'asc' })
  assert.deepEqual(readChannelSort(null), CHANNEL_SORT_DEFAULT)
  assert.deepEqual(readChannelSort('nonsense'), CHANNEL_SORT_DEFAULT)
  assert.deepEqual(readChannelSort('toString-asc'), CHANNEL_SORT_DEFAULT)
  assert.deepEqual(readChannelSort('name-sideways'), CHANNEL_SORT_DEFAULT)
})

test('a column opens the way it reads best', () => {
  assert.equal(firstDirection('name'), 'asc')
  assert.equal(firstDirection('category'), 'asc')
  assert.equal(firstDirection('last'), 'desc')
  assert.equal(firstDirection('quiet'), 'desc')
})

// ── Filtering ───────────────────────────────────────────────────────────────────────────────────

const chip = (property: string, operator: FilterChip['operator'], ...values: string[]): FilterChip => ({
  property,
  operator,
  values,
})

const kept = (chips: FilterChip[], search = '') => ids(filterChannels(ROWS, chips, search, NOW))

test('no filters keep every channel', () => {
  assert.deepEqual(kept([]), ids(ROWS))
})

test('Quiet for keeps channels quiet at least that many days, nobody-wrote included, unknown times out', () => {
  assert.deepEqual(kept([chip('quiet', 'is', '30')]), ['old', 'never'])
  assert.deepEqual(kept([chip('quiet', 'is', '1000')]), ['never'])
  assert.deepEqual(kept([chip('quiet', 'is', '1')]), ['recent', 'old', 'never'])
})

test('Category keeps, or leaves out, the channels in the categories picked; none is its own choice', () => {
  assert.deepEqual(kept([chip('category', 'is', 'c1')]), ['old', 'reading'])
  assert.deepEqual(kept([chip('category', 'is', 'c1', NO_CATEGORY)]), ['old', 'never', 'reading', 'locked'])
  assert.deepEqual(kept([chip('category', 'is-not', 'c1')]), ['recent', 'never', 'locked'])
})

test('Type and Status are choices', () => {
  const forum = [channel({ id: 'f', type: 'forum' }), channel({ id: 't' })]
  assert.deepEqual(ids(filterChannels(forum, [chip('type', 'is', 'forum')], '', NOW)), ['f'])
  assert.deepEqual(kept([chip('status', 'is', 'cant-read')]), ['locked'])
  assert.deepEqual(kept([chip('status', 'is', 'no-messages', 'still-reading')]), ['never', 'reading'])
  assert.deepEqual(kept([chip('status', 'is-not', 'has-messages')]), ['never', 'reading', 'locked'])
})

test('a channel’s status is what its Quiet for says when it has no time', () => {
  assert.equal(channelStatus(channel({ lastMessageAt: '2026-10-01T12:00:00Z' })), 'has-messages')
  assert.equal(channelStatus(channel()), 'no-messages')
  assert.equal(channelStatus(channel({ stillReading: true })), 'still-reading')
  assert.equal(channelStatus(channel({ canRead: false })), 'cant-read')
})

test('Staff-only is yes or no', () => {
  assert.deepEqual(kept([chip('staffOnly', 'yes')]), ['locked'])
  assert.deepEqual(kept([chip('staffOnly', 'no')]), ['recent', 'old', 'never', 'reading'])
})

test('the search words match the name anywhere, whatever the case', () => {
  assert.deepEqual(kept([], 'GAMMA'), ['never', 'reading'])
  assert.deepEqual(kept([], '  alp '), ['old'])
})

test('filters combine', () => {
  assert.deepEqual(kept([chip('quiet', 'is', '30'), chip('category', 'is', 'c1')]), ['old'])
  assert.deepEqual(kept([chip('quiet', 'is', '30')], 'gamma'), ['never'])
})

test('Seen by is the server’s to work out: any of the roles, or none of them', () => {
  assert.deepEqual(channelQueryFrom([]), {})
  assert.deepEqual(channelQueryFrom([chip('seenBy', 'is', '1', '2')]), { seenBy: ['1', '2'] })
  assert.deepEqual(channelQueryFrom([chip('seenBy', 'is-not', '1')]), { notSeenBy: ['1'] })
  assert.deepEqual(channelQueryFrom([chip('seenBy', 'is')]), {})
  // And the rows are not narrowed by it here.
  assert.deepEqual(kept([chip('seenBy', 'is', '1')]), ids(ROWS))
})
