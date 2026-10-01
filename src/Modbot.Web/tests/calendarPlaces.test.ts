import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  counted,
  DESCRIPTION_LIMIT,
  isSetUp,
  missingChannel,
  notSetUp,
  TITLE_LIMIT,
  wantedPlaces,
} from '../src/lib/calendarPlaces.ts'

/**
 * Where an event goes and whether each place is set up (calendar design §14): the form's chips,
 * the "Not set up" marks, and the counters beside the title and the description.
 */

const ticks = { publishToVRChat: true, publishToDiscord: true, postToChannel: true, autoOpen: true }

test('the ticked places come in the chips’ order', () => {
  assert.deepEqual(wantedPlaces(ticks), ['vrchat', 'discordEvent', 'channelPost', 'instance'])
  assert.deepEqual(wantedPlaces({ ...ticks, publishToVRChat: false, postToChannel: false }), ['discordEvent', 'instance'])
})

test('VRChat covers the calendar and opening the instance; Discord covers the event and the post', () => {
  const ready = { vrChat: false, discord: true }

  assert.equal(isSetUp('vrchat', ready), false)
  assert.equal(isSetUp('instance', ready), false)
  assert.equal(isSetUp('discordEvent', ready), true)
  assert.equal(isSetUp('channelPost', ready), true)
})

test('only ticked places that are not set up are marked', () => {
  assert.deepEqual(notSetUp({ ...ticks, autoOpen: false }, { vrChat: true, discord: false }), ['discordEvent', 'channelPost'])
  assert.deepEqual(notSetUp(ticks, { vrChat: true, discord: true }), [])
})

test('a server that does not say marks nothing, rather than everything', () => {
  assert.deepEqual(notSetUp(ticks, undefined), [])
  assert.deepEqual(notSetUp(ticks, null), [])
})

test('a channel post with no channel is the form’s own missing piece', () => {
  assert.equal(missingChannel({ postToChannel: true, channelId: null }), true)
  assert.equal(missingChannel({ postToChannel: true, channelId: '222' }), false)
  assert.equal(missingChannel({ postToChannel: false, channelId: null }), false)
})

test('the counters count what the server counts, and say when it is over', () => {
  assert.deepEqual(counted('  Movie night  ', TITLE_LIMIT), { label: '11 / 100', over: false })
  assert.deepEqual(counted('a'.repeat(101), TITLE_LIMIT), { label: '101 / 100', over: true })
  assert.deepEqual(counted('d'.repeat(DESCRIPTION_LIMIT), DESCRIPTION_LIMIT), { label: '1000 / 1000', over: false })
})
