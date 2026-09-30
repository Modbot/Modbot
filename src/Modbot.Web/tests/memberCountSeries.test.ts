import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  MEMBER_COUNT_RANGES,
  readingTime,
  timeLabel,
  timeTicks,
  toRows,
} from '../src/pages/analytics/memberCountSeries.ts'

const utc = { locale: 'en-GB', timeZone: 'UTC' }
const DAY = 86_400_000
const at = Date.UTC(2026, 5, 16, 14, 5)

test('the four ranges match the API, in the order the buttons show them', () => {
  assert.deepEqual(
    MEMBER_COUNT_RANGES.map((r) => r.range),
    ['day', 'week', 'month', 'all'],
  )
})

test('rows carry the reading time as a number, in time order, and drop anything unreadable', () => {
  const rows = toRows([
    { at: '2026-06-16T14:10:00Z', members: 8124, online: 41 },
    { at: 'not a time', members: 0, online: 0 },
    { at: '2026-06-16T14:05:00Z', members: 8123, online: 40 },
  ])

  assert.deepEqual(rows, [
    { at: at, members: 8123, online: 40 },
    { at: at + 5 * 60_000, members: 8124, online: 41 },
  ])
})

test('an axis too short for two whole minutes gets evenly spaced ticks at both ends', () => {
  assert.deepEqual(timeTicks(0, 1000, 6, utc), [0, 200, 400, 600, 800, 1000])
  assert.deepEqual(timeTicks(5, 5), [5])
})

test('a week gets a tick at every midnight, so no day is left without its label', () => {
  const from = Date.UTC(2026, 8, 20, 14, 5)
  const ticks = timeTicks(from, from + 7 * DAY, 6, utc)

  assert.deepEqual(
    ticks,
    [21, 22, 23, 24, 25, 26, 27].map((d) => Date.UTC(2026, 8, d)),
  )
  assert.deepEqual(
    ticks.map((t) => timeLabel(t, 7 * DAY, utc)),
    // British English writes September as "Sept" (CLDR since 38), where every other month is three letters.
    ['21 Sept', '22 Sept', '23 Sept', '24 Sept', '25 Sept', '26 Sept', '27 Sept'],
  )
})

test('a week names seven days in a row whatever hour it starts at', () => {
  for (let hour = 0; hour < 24; hour++) {
    const from = Date.UTC(2026, 8, 20, hour, 30)
    const days = timeTicks(from, from + 7 * DAY, 6, utc).map((t) => new Date(t).getUTCDate())

    assert.deepEqual(days, [21, 22, 23, 24, 25, 26, 27], `starting at ${hour}:30`)
  }
})

test('a day gets a tick every few hours, on the hour', () => {
  const from = Date.UTC(2026, 8, 26, 14, 5)
  const ticks = timeTicks(from, from + DAY, 6, utc)

  assert.deepEqual(
    ticks.map((t) => timeLabel(t, DAY, utc)),
    ['15:00', '18:00', '21:00', '00:00', '03:00', '06:00', '09:00', '12:00'],
  )
})

test('hour ticks fall on the hour of the viewer’s clock, even half an hour off UTC', () => {
  const india = { locale: 'en-GB', timeZone: 'Asia/Kolkata' }
  const from = Date.UTC(2026, 8, 26, 14, 5)
  const labels = timeTicks(from, from + DAY, 6, india).map((t) => timeLabel(t, DAY, india))

  assert.ok(labels.length >= 4)
  for (const label of labels) assert.match(label, /^(00|03|06|09|12|15|18|21):00$/)
})

test('a short axis gets ticks on whole minutes', () => {
  const from = Date.UTC(2026, 8, 26, 14, 7, 30)
  const ticks = timeTicks(from, from + 40 * 60_000, 6, utc)

  assert.deepEqual(
    ticks.map((t) => timeLabel(t, 40 * 60_000, utc)),
    ['14:10', '14:15', '14:20', '14:25', '14:30', '14:35', '14:40', '14:45'],
  )
})

test('a month gets a tick every week, ending on the newest day', () => {
  const to = Date.UTC(2026, 8, 27, 14, 5)
  const ticks = timeTicks(to - 30 * DAY, to, 6, utc)

  assert.deepEqual(ticks, [
    Date.UTC(2026, 7, 30),
    Date.UTC(2026, 8, 6),
    Date.UTC(2026, 8, 13),
    Date.UTC(2026, 8, 20),
    Date.UTC(2026, 8, 27),
  ])
})

test('years of history get a tick on the first of every few months', () => {
  const to = Date.UTC(2026, 8, 27, 14, 5)
  const from = Date.UTC(2023, 8, 27, 14, 5)
  const ticks = timeTicks(from, to, 6, utc)

  assert.deepEqual(
    ticks.map((t) => timeLabel(t, to - from, utc)),
    ['Jan 2024', 'Jul 2024', 'Jan 2025', 'Jul 2025', 'Jan 2026', 'Jul 2026'],
  )
})

test('midnight is the viewer’s own midnight, not UTC’s', () => {
  const from = Date.UTC(2026, 8, 20, 18)
  const ticks = timeTicks(from, from + 7 * DAY, 6, { timeZone: 'America/New_York' })

  // New York is four hours behind UTC in September.
  assert.deepEqual(
    ticks,
    [21, 22, 23, 24, 25, 26, 27].map((d) => Date.UTC(2026, 8, d, 4)),
  )
})

test('midnight ticks stay on midnight across a clock change', () => {
  // The UK's clocks go back an hour early on 25 October 2026.
  const from = Date.UTC(2026, 9, 21, 12)
  const ticks = timeTicks(from, from + 7 * DAY, 6, { timeZone: 'Europe/London' })

  assert.deepEqual(ticks, [
    Date.UTC(2026, 9, 21, 23),
    Date.UTC(2026, 9, 22, 23),
    Date.UTC(2026, 9, 23, 23),
    Date.UTC(2026, 9, 24, 23),
    Date.UTC(2026, 9, 26),
    Date.UTC(2026, 9, 27),
    Date.UTC(2026, 9, 28),
  ])
})

test('an axis label is the time of day across a day, the day across a month, the month across years', () => {
  assert.equal(timeLabel(at, DAY, utc), '14:05')
  assert.equal(timeLabel(at, 30 * DAY, utc), '16 Jun')
  assert.equal(timeLabel(at, 3 * 365 * DAY, utc), 'Jun 2026')
})

test('the tooltip writes the whole reading time, with the year when it is not this year', () => {
  assert.equal(readingTime(at, { ...utc, now: Date.UTC(2027, 0, 5) }), '16 Jun 2026, 14:05')
})

test('the tooltip leaves the year out in the current year', () => {
  assert.equal(readingTime(at, { ...utc, now: Date.UTC(2026, 8, 26) }), '16 Jun, 14:05')
})
