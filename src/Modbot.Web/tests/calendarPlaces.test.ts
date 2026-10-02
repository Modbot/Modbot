import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { CalendarEvent } from '../src/lib/calendar.ts'
import {
  anySending,
  counted,
  DESCRIPTION_LIMIT,
  isSetUp,
  missingChannel,
  notSetUp,
  placeLines,
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

// ── What is listed under an event (calendar design §17.3) ──────────────────────────────────────

type Row = CalendarEvent['places'][number]

const row = (place: Row['place'], state: Row['state']): Row => ({
  place,
  state,
  error: state === 'failed' ? 'No.' : null,
  errorAt: null,
  updatedAt: '2026-10-02T18:00:00Z',
})

const event = (over: Partial<Pick<CalendarEvent, 'state' | 'places' | 'channelId'>> = {}) => ({
  state: 'scheduled' as CalendarEvent['state'],
  places: [] as Row[],
  publishToVRChat: true,
  publishToDiscord: true,
  postToChannel: true,
  channelId: '222' as string | null,
  autoOpen: false,
  ...over,
})

const ready = { vrChat: true, discord: true }

test('a place just ticked shows as being sent before Modbot has a row for it', () => {
  const lines = placeLines(event(), ready)

  assert.deepEqual(
    lines.map((l) => [l.place, l.state, l.row]),
    [
      ['vrchat', 'waiting', null],
      ['discordEvent', 'waiting', null],
      ['channelPost', 'waiting', null],
    ],
  )
  assert.equal(anySending(lines), true)
})

test('the server’s rows are listed as they stand, in the chips’ order, and nothing is sending once they are done', () => {
  const lines = placeLines(
    event({ places: [row('channelPost', 'published'), row('vrchat', 'failed'), row('discordEvent', 'published')] }),
    ready,
  )

  assert.deepEqual(
    lines.map((l) => [l.place, l.state]),
    [
      ['vrchat', 'failed'],
      ['discordEvent', 'published'],
      ['channelPost', 'published'],
    ],
  )
  assert.equal(anySending(lines), false)
})

test('a place that is not set up is left out, for its "Not set up" line', () => {
  assert.deepEqual(
    placeLines(event(), { vrChat: false, discord: true }).map((l) => l.place),
    ['discordEvent', 'channelPost'],
  )
})

test('a draft or a cancelled event lists only the rows it has', () => {
  assert.deepEqual(placeLines(event({ state: 'draft' }), ready), [])
  assert.deepEqual(
    placeLines(event({ state: 'cancelled', places: [row('cancelPost', 'waiting')] }), ready).map((l) => [l.place, l.state]),
    [['cancelPost', 'waiting']],
  )
})

test('a channel post with no channel is not listed as being sent', () => {
  assert.deepEqual(
    placeLines(event({ channelId: null }), ready).map((l) => l.place),
    ['vrchat', 'discordEvent'],
  )
})
