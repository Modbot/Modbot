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

test('ticks are evenly spaced and include both ends', () => {
  const ticks = timeTicks(0, 1000, 6)

  assert.deepEqual(ticks, [0, 200, 400, 600, 800, 1000])
  assert.deepEqual(timeTicks(5, 5), [5])
  assert.equal(timeTicks(0, 7 * DAY).length, 6)
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
