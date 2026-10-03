import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { CalendarEvent } from '../src/lib/calendar.ts'
import {
  anySending,
  counted,
  DESCRIPTION_LIMIT,
  DESTINATION_LABEL,
  DESTINATIONS,
  googleChip,
  googleTicked,
  isSetUp,
  membersOnly,
  missingChannel,
  notSetUp,
  placeLines,
  showsVisibleTo,
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

// ── Google Calendar (Google Calendar design, step 2) ───────────────────────────────────────────

const google = (over: { publishToGoogle?: boolean; visibility?: string; places?: Row[] } = {}) => ({
  ...event({ places: over.places ?? [] }),
  publishToVRChat: false,
  publishToDiscord: false,
  postToChannel: false,
  publishToGoogle: over.publishToGoogle ?? true,
  visibility: over.visibility ?? 'public',
})

test('an event ticked for Google Calendar shows it being sent, after the Discord places', () => {
  const lines = placeLines(google(), { vrChat: true, discord: true, google: true })

  assert.deepEqual(lines.map((l) => [l.place, l.state]), [['googleCalendar', 'waiting']])
})

test('a members-only event is never shown as going to Google Calendar, nor as not set up for it', () => {
  assert.deepEqual(placeLines(google({ visibility: 'group' }), { vrChat: true, discord: true, google: true }), [])
  assert.deepEqual(notSetUp(google({ visibility: 'group' }), { vrChat: true, discord: true, google: false }), [])
})

test('an event VRChat shows only to some roles is members-only, as the server says', () => {
  const staff = { ...google(), vrChatRoleIds: ['grol_staff'] }

  assert.equal(membersOnly({ visibility: 'public', vrChatRoleIds: ['grol_staff'] }), true)
  assert.equal(membersOnly({ visibility: 'public', vrChatRoleIds: [] }), false)
  assert.equal(membersOnly({ visibility: 'public', vrChatRoleIds: null }), false)
  assert.deepEqual(placeLines(staff, { vrChat: true, discord: true, google: true }), [])
  assert.deepEqual(notSetUp(staff, { vrChat: true, discord: true, google: false }), [])
})

test('Google Calendar not set up, or Sending off, says Not set up for a ticked event', () => {
  const off = { vrChat: true, discord: true, google: false }

  assert.deepEqual(notSetUp(google(), off), ['googleCalendar'])
  assert.deepEqual(placeLines(google(), off), [])

  // An older server says nothing about Google: nothing is marked.
  assert.deepEqual(notSetUp(google(), { vrChat: true, discord: true }), [])
})

test('the Google Calendar row is listed as it stands', () => {
  const lines = placeLines(google({ places: [row('googleCalendar', 'failed')] }), { vrChat: true, discord: true, google: true })

  assert.deepEqual(lines.map((l) => [l.place, l.state]), [['googleCalendar', 'failed']])
  assert.equal(isSetUp('vrchat', { vrChat: true, discord: true, google: false }), true)
})

// ── The form's Google Calendar chip (Google Calendar design, step 3) ───────────────────────────

const googleReady = { vrChat: true, discord: true, google: true }
const googleOff = { vrChat: true, discord: true, google: false }

test('a new event everyone may see starts ticked for Google Calendar once it is ready', () => {
  assert.deepEqual(googleChip({ visibility: 'public' }, null, googleReady, true), { on: true, disabled: false, note: null })
  assert.equal(googleTicked({ visibility: 'public' }, null, googleReady, true), true)
})

test('a new event starts unticked while Google Calendar is not ready, and says Not set up', () => {
  assert.deepEqual(googleChip({ visibility: 'public' }, null, googleOff, true), { on: false, disabled: false, note: 'notSetUp' })
  assert.equal(googleTicked({ visibility: 'public' }, null, googleOff, true), false)

  // Ticked by hand while not ready: kept, and still Not set up.
  assert.deepEqual(googleChip({ visibility: 'public', publishToGoogle: true }, null, googleOff, true), {
    on: true,
    disabled: false,
    note: 'notSetUp',
  })
})

test('a members-only event cannot be ticked for Google Calendar, and says Members only', () => {
  assert.deepEqual(googleChip({ visibility: 'group' }, null, googleReady, true), { on: false, disabled: true, note: 'membersOnly' })
  assert.equal(googleTicked({ visibility: 'group' }, null, googleReady, true), false)

  // Shown on VRChat only to some roles: members-only too, whatever Visible to says.
  assert.deepEqual(googleChip({ visibility: 'public', publishToGoogle: true }, ['grol_staff'], googleReady, false), {
    on: false,
    disabled: true,
    note: 'membersOnly',
  })
})

test('a tick kept on an event made members-only comes back when everyone may see it again', () => {
  const ticked = { publishToGoogle: true }

  assert.equal(googleChip({ ...ticked, visibility: 'group' }, null, googleReady, false).on, false)
  assert.equal(googleTicked({ ...ticked, visibility: 'group' }, null, googleReady, false), true)
  assert.equal(googleChip({ ...ticked, visibility: 'public' }, null, googleReady, false).on, true)
})

test('a saved event keeps its own tick, and a chip clicked off stays off', () => {
  assert.equal(googleChip({ visibility: 'public', publishToGoogle: false }, null, googleReady, false).on, false)
  assert.equal(googleChip({ visibility: 'public', publishToGoogle: false }, null, googleReady, true).on, false)
  // From an older server, with nothing said: not ticked by the form.
  assert.equal(googleChip({ visibility: 'public', publishToGoogle: null }, null, googleReady, false).on, false)
})

test('an older server that says nothing about Google marks nothing on the chip', () => {
  assert.equal(googleChip({ visibility: 'public' }, null, { vrChat: true, discord: true }, true).note, null)
  assert.equal(googleChip({ visibility: 'public' }, null, null, true).note, null)
})

test('Visible to shows with the VRChat calendar, with Google Calendar, and while Members only holds Google back', () => {
  const vrchatOff = { publishToVRChat: false }

  assert.equal(showsVisibleTo({ publishToVRChat: true }, googleChip({ visibility: 'group' }, null, googleOff, true)), true)
  assert.equal(showsVisibleTo(vrchatOff, googleChip({ visibility: 'public' }, null, googleReady, true)), true)
  assert.equal(showsVisibleTo(vrchatOff, googleChip({ visibility: 'group' }, null, googleReady, true)), true)

  // Neither: nothing goes by it.
  assert.equal(showsVisibleTo(vrchatOff, googleChip({ visibility: 'public', publishToGoogle: false }, null, googleReady, false)), false)
  assert.equal(showsVisibleTo(vrchatOff, googleChip({ visibility: 'group' }, null, googleOff, true)), false)
})

test('the Google Calendar chip comes after the calendar feed', () => {
  const feed = DESTINATIONS.indexOf('feed')

  assert.deepEqual(DESTINATIONS.slice(feed, feed + 2), ['feed', 'googleCalendar'])
  assert.equal(DESTINATION_LABEL.googleCalendar, 'Google Calendar')
})
