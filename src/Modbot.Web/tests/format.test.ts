import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  ago,
  clockTime,
  duration,
  formatDay,
  formatDayRange,
  howLong,
  lengthOfTime,
  needsYear,
  plural,
} from '../src/lib/format.ts'

const NOW = '2026-09-18T12:00:00Z'

test('how long says the same steps as ago, without the ago', () => {
  assert.equal(howLong('2026-09-18T11:59:30Z', NOW), '30s')
  assert.equal(howLong('2026-09-18T11:30:00Z', NOW), '30m')
  assert.equal(howLong('2026-09-18T06:00:00Z', NOW), '6h')
  assert.equal(howLong('2026-06-20T12:00:00Z', NOW), '90d')

  assert.equal(ago('2026-06-20T12:00:00Z', NOW), '90d ago')
})

test('somebody Modbot has no record of has not been known for any time at all', () => {
  assert.equal(howLong(null, NOW), 'never')
})

test('a clock that ran backwards does not produce a negative age', () => {
  assert.equal(howLong('2026-09-18T12:00:30Z', NOW), '0s')
})

// Written without naming a locale or a time zone, because clockTime uses the viewer's: the tests
// hold on any machine they run on.
test('a clock time has no seconds: two instants in the same minute read the same', () => {
  assert.equal(clockTime('2026-09-18T15:41:07Z'), clockTime('2026-09-18T15:41:52Z'))
})

test('a clock time still tells one minute from the next', () => {
  assert.notEqual(clockTime('2026-09-18T15:41:07Z'), clockTime('2026-09-18T15:42:07Z'))
})

test('a clock time is the time alone, with no date in it', () => {
  assert.equal(clockTime('2026-09-18T15:41:00Z'), clockTime('2026-09-19T15:41:00Z'))
})

// ── Lengths of time ──────────────────────────────────────────────────────────────────────────

test('a length of time is whole units, never a decimal', () => {
  assert.equal(lengthOfTime(16), '16 min')
  assert.equal(lengthOfTime(186), '3 h 6 min')
  assert.equal(lengthOfTime(84), '1 h 24 min')
  assert.equal(lengthOfTime(52 * 60), '2 d 4 h')
})

test('the smaller unit is left out when it is zero', () => {
  assert.equal(lengthOfTime(180), '3 h')
  assert.equal(lengthOfTime(48 * 60), '2 d')
})

test('a length of time is rounded before its unit is chosen', () => {
  assert.equal(lengthOfTime(59.7), '1 h')
  assert.equal(lengthOfTime(24 * 60 - 0.2), '1 d')
  assert.equal(lengthOfTime(24 * 60 + 40), '1 d 1 h')
})

test('a duration in seconds says seconds under a minute, and whole units after', () => {
  assert.equal(duration(1), '1 second')
  assert.equal(duration(45), '45 seconds')
  assert.equal(duration(59.6), '1 min')
  assert.equal(duration(720), '12 min')
  assert.equal(duration(5400), '1 h 30 min')
})

// ── Plurals ──────────────────────────────────────────────────────────────────────────────────

test('only exactly one takes the singular', () => {
  assert.equal(plural(1, 'action'), 'action')
  assert.equal(plural(2, 'action'), 'actions')
  assert.equal(plural(0, 'action'), 'actions')
  assert.equal(plural(1.5, 'hour'), 'hours')
})

test('a word that does not just take an s is given both ways', () => {
  assert.equal(plural(1, 'person', 'people'), 'person')
  assert.equal(plural(3, 'person', 'people'), 'people')
})

// ── The year ─────────────────────────────────────────────────────────────────────────────────

// Built from the machine's own year, because the rule is about the viewer's current year: the
// tests hold in any year they run in. Local noon, so no time zone moves a day across New Year.
const thisYear = new Date().getFullYear()
const lastYear = thisYear - 1

test('a day in the current year is written without the year', () => {
  assert.equal(needsYear(`${thisYear}-03-03T12:00:00`), false)
  assert.ok(!formatDay(`${thisYear}-03-03T12:00:00`).includes(String(thisYear)))
})

test('a day in another year keeps its year', () => {
  assert.equal(needsYear(`${lastYear}-03-03T12:00:00`), true)
  assert.ok(formatDay(`${lastYear}-03-03T12:00:00`).includes(String(lastYear)))
})

test('the year can be asked for, for a date shown beside others that need it', () => {
  assert.ok(formatDay(`${thisYear}-03-03T12:00:00`, true).includes(String(thisYear)))
})

test('a range in the current year has no year at either end', () => {
  const range = formatDayRange(`${thisYear}-01-10T12:00:00`, `${thisYear}-02-10T12:00:00`)
  assert.ok(!range.includes(String(thisYear)))
  assert.ok(range.includes(' – '))
})

test('a range over New Year keeps both years', () => {
  const range = formatDayRange(`${lastYear}-12-15T12:00:00`, `${thisYear}-01-10T12:00:00`)
  assert.ok(range.includes(String(lastYear)))
  assert.ok(range.includes(String(thisYear)))
})

test('several dates need the year if any one of them does', () => {
  assert.equal(needsYear(`${thisYear}-01-10T12:00:00`, `${thisYear}-02-10T12:00:00`), false)
  assert.equal(needsYear(`${lastYear}-12-15T12:00:00`, `${thisYear}-01-10T12:00:00`), true)
})
