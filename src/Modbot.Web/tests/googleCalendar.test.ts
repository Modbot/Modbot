import assert from 'node:assert/strict'
import { test } from 'node:test'
import { addToGoogleLink, googleRepeatWords } from '../src/lib/googleCalendar.ts'

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
