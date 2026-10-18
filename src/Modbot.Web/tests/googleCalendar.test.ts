import assert from 'node:assert/strict'
import { test } from 'node:test'
import { addToGoogleLink, googleRepeatWords, googleWhen } from '../src/lib/googleCalendar.ts'
import { GOOGLE_COLOURS, googleColourHex, googleColourName, CALENDAR_DEFAULT_COLOUR } from '../src/lib/googleColours.ts'

/**
 * Google Calendar on the calendar page (Google Calendar design §3.8, §3.9): the Preview's repeat in
 * words, and the feed row's "Add to Google Calendar".
 */

const repeat = (over: Partial<Parameters<typeof googleRepeatWords>[0]> = {}) => ({
  frequency: 'weekly',
  every: 1,
  days: ['MO'],
  until: null,
  times: null,
  ...over,
})

test('a weekly repeat names its days', () => {
  assert.equal(googleRepeatWords(repeat()), 'Weekly on Monday')
  assert.equal(googleRepeatWords(repeat({ days: ['MO', 'WE', 'FR'] })), 'Weekly on Monday, Wednesday and Friday')
})

test('every few days, weeks or months says how many', () => {
  assert.equal(googleRepeatWords(repeat({ every: 2, days: ['MO', 'TH'] })), 'Every 2 weeks on Monday and Thursday')
  assert.equal(googleRepeatWords(repeat({ frequency: 'daily', every: 3, days: [] })), 'Every 3 days')
  assert.equal(googleRepeatWords(repeat({ frequency: 'monthly', days: [] })), 'Monthly')
})

test('the end is the last day or the number of times', () => {
  assert.equal(googleRepeatWords(repeat({ until: '2026-12-31' })), 'Weekly on Monday, until 2026-12-31')
  assert.equal(googleRepeatWords(repeat({ frequency: 'daily', days: [], times: 6 })), 'Daily, 6 times')
})

test('days sent with a daily or monthly repeat are not named', () => {
  assert.equal(googleRepeatWords(repeat({ frequency: 'daily', days: ['MO'] })), 'Daily')
})

test('Add to Google Calendar hands Google the feed as a webcal address', () => {
  const link = addToGoogleLink('https://modbot.example/api/calendar/feed/abc.ics')

  assert.equal(link, `https://calendar.google.com/calendar/r?cid=${encodeURIComponent('webcal://modbot.example/api/calendar/feed/abc.ics')}`)
  assert.equal(
    new URL(link!).searchParams.get('cid'),
    'webcal://modbot.example/api/calendar/feed/abc.ics',
  )
})

test('a plain http feed address is handed over as webcal too, and a non-web one not at all', () => {
  assert.equal(
    new URL(addToGoogleLink('http://localhost:8080/api/calendar/feed/abc.ics')!).searchParams.get('cid'),
    'webcal://localhost:8080/api/calendar/feed/abc.ics',
  )
  assert.equal(addToGoogleLink('not an address'), null)
  assert.equal(addToGoogleLink('javascript:alert(1)'), null)
})

test('the popup date line is the day, then the times, in the event zone', () => {
  assert.match(
    googleWhen('2026-10-16T19:00:00Z', '2026-10-16T21:00:00Z', 'America/New_York', 'en-US'),
    /^Friday, October 16 ⋅ 3:00.PM – 5:00.PM$/,
  )
})

test('an event that runs past midnight in its zone names both days', () => {
  const line = googleWhen('2026-10-16T23:00:00Z', '2026-10-17T01:00:00Z', 'UTC', 'en-US')
  assert.match(line, /^Friday, October 16 ⋅ 11:00.PM – Saturday, October 17 ⋅ 1:00.AM$/)
})

test('a zone the browser does not know is read in the viewer own', () => {
  assert.match(googleWhen('2026-10-16T19:00:00Z', '2026-10-16T21:00:00Z', 'Mars/Olympus', 'en-US'), /^\w+day, \w+ \d+ ⋅ /)
})

test('Google has eleven event colours with their own names, and no pick draws the calendar default', () => {
  assert.deepEqual(
    GOOGLE_COLOURS.map((c) => c.id),
    ['1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11'],
  )
  assert.equal(new Set(GOOGLE_COLOURS.map((c) => c.name)).size, 11)
  assert.ok(GOOGLE_COLOURS.every((c) => /^#[0-9a-f]{6}$/.test(c.hex)))
  assert.equal(googleColourName('11'), 'Tomato')
  assert.equal(googleColourHex('9'), '#5484ed')
  assert.equal(googleColourName(''), 'Calendar default')
  assert.equal(googleColourHex(null), CALENDAR_DEFAULT_COLOUR)
  assert.equal(googleColourHex('12'), CALENDAR_DEFAULT_COLOUR)
})
