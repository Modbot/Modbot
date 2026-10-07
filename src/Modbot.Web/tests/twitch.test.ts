import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { LiveEvent } from '../src/lib/liveStream.ts'
import { changesTwitch } from '../src/lib/liveRules.ts'
import { liveFor, placesEqual, tickedSites, TWITCH_WORDS } from '../src/lib/twitch.ts'
import type { TwitchPostPlaces } from '../src/lib/api.ts'

const NONE: TwitchPostPlaces = { discord: null, vrChat: null, bluesky: false }

test('the post template offers the stream title, the category and the link', () => {
  assert.deepEqual(
    TWITCH_WORDS.map((w) => w.word),
    ['{title}', '{category}', '{link}'],
  )
})

test('nothing is ticked to start, and places with the same ticks are equal', () => {
  assert.deepEqual(tickedSites(NONE), [])
  assert.ok(placesEqual(NONE, { discord: null, vrChat: null, bluesky: false }))
})

test('a tick, a channel, a role or a notify makes places differ', () => {
  const discord = { channelId: '222', roleId: null, publish: false }
  const ticked: TwitchPostPlaces = { ...NONE, discord }

  assert.ok(!placesEqual(NONE, ticked))
  assert.ok(!placesEqual(ticked, { ...NONE, discord: { ...discord, channelId: '333' } }))
  assert.ok(!placesEqual(ticked, { ...NONE, discord: { ...discord, roleId: '9' } }))
  assert.ok(!placesEqual(ticked, { ...NONE, discord: { ...discord, publish: true } }))
  assert.ok(!placesEqual(NONE, { ...NONE, bluesky: true }))
  assert.ok(!placesEqual({ ...NONE, vrChat: { visibility: 'group', roleIds: null, notify: false } }, { ...NONE, vrChat: { visibility: 'group', roleIds: null, notify: true } }))
})

test('roles left out and an empty list are the same, in any order', () => {
  const a: TwitchPostPlaces = { ...NONE, vrChat: { visibility: 'group', roleIds: null, notify: false } }
  const b: TwitchPostPlaces = { ...NONE, vrChat: { visibility: 'group', roleIds: [], notify: false } }
  const c: TwitchPostPlaces = { ...NONE, vrChat: { visibility: 'group', roleIds: ['x', 'y'], notify: false } }
  const d: TwitchPostPlaces = { ...NONE, vrChat: { visibility: 'group', roleIds: ['y', 'x'], notify: false } }

  assert.ok(placesEqual(a, b))
  assert.ok(placesEqual(c, d))
  assert.ok(!placesEqual(a, c))
})

test('the sites ticked are named in the order the settings list them', () => {
  assert.deepEqual(
    tickedSites({ discord: { channelId: '1', roleId: null, publish: false }, vrChat: { visibility: 'public', roleIds: null, notify: false }, bluesky: true }),
    ['Discord', 'VRChat', 'Bluesky'],
  )
  assert.deepEqual(tickedSites({ ...NONE, bluesky: true }), ['Bluesky'])
})

test('how long a stream has been live', () => {
  const start = Date.parse('2026-10-07T19:00:00Z')

  assert.equal(liveFor('2026-10-07T19:00:00Z', start + 20_000), 'Just now')
  assert.equal(liveFor('2026-10-07T19:00:00Z', start + 12 * 60_000), '12 min')
  assert.equal(liveFor('2026-10-07T19:00:00Z', start + 60 * 60_000), '1 h')
  assert.equal(liveFor('2026-10-07T19:00:00Z', start + 65 * 60_000), '1 h 5 min')
  assert.equal(liveFor('2026-10-07T19:00:00Z', start - 60_000), 'Just now')
})

test('the Live on Twitch card redraws for the Twitch facts and nothing else', () => {
  const event = (type: string) => ({ type }) as LiveEvent

  assert.ok(changesTwitch(event('modbot.twitch.online')))
  assert.ok(changesTwitch(event('modbot.twitch.update')))
  assert.ok(changesTwitch(event('modbot.twitch.offline')))
  assert.ok(!changesTwitch(event('modbot.post.send')))
  assert.ok(!changesTwitch(event('vrchat.instance.join')))
})
