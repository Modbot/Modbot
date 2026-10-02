import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ROLE_FLAG_LABELS, quietFor } from '../src/lib/discordTidyUp.ts'
import type { QuietChannelRow } from '../src/lib/api.ts'

const NOW = '2026-10-02T12:00:00Z'

function channel(over: Partial<QuietChannelRow> = {}): QuietChannelRow {
  return {
    id: '1',
    name: 'general',
    type: 'text',
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
