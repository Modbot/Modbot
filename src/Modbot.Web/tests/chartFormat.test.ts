import assert from 'node:assert/strict'
import { test } from 'node:test'
import { dateTime, dayRange, longDay, minutes } from '../src/components/charts/format.ts'

// ── Minutes ──────────────────────────────────────────────────────────────────────────────────

test('minutes read as hours and minutes, not decimal hours', () => {
  assert.equal(minutes(186), '3 h 6 min')
  assert.equal(minutes(84), '1 h 24 min')
  assert.equal(minutes(16), '16 min')
})

test('past a day, minutes read as days and hours', () => {
  assert.equal(minutes(52 * 60), '2 d 4 h')
  assert.equal(minutes(72 * 60), '3 d')
})

test('whole hours drop the minutes', () => {
  assert.equal(minutes(180), '3 h')
})

test('the edges of minutes are unchanged', () => {
  assert.equal(minutes(0.5), 'under a minute')
  assert.equal(minutes(-1), '—')
  assert.equal(minutes(Number.NaN), '—')
})

// ── The year ─────────────────────────────────────────────────────────────────────────────────

// From the machine's own year, so the tests hold whatever year they run in.
const thisYear = new Date().getFullYear()
const lastYear = thisYear - 1

test('a chart day in the current year has no year', () => {
  assert.ok(!longDay(`${thisYear}-06-16`).includes(String(thisYear)))
})

test('a chart day in another year keeps it', () => {
  assert.ok(longDay(`${lastYear}-06-16`).includes(String(lastYear)))
})

test('a range of chart days in the current year has no year at either end', () => {
  const range = dayRange(`${thisYear}-08-28`, `${thisYear}-09-26`)
  assert.ok(!range.includes(String(thisYear)))
  assert.equal(range, `${longDay(`${thisYear}-08-28`)} – ${longDay(`${thisYear}-09-26`)}`)
})

test('a range of chart days over New Year keeps both years', () => {
  const range = dayRange(`${lastYear}-12-15`, `${thisYear}-01-10`)
  assert.ok(range.includes(String(lastYear)))
  assert.ok(range.includes(String(thisYear)))
})

test('an instant in the current year has no year; one from another year does', () => {
  assert.ok(!dateTime(`${thisYear}-06-16T12:00:00`).includes(String(thisYear)))
  assert.ok(dateTime(`${lastYear}-06-16T12:00:00`).includes(String(lastYear)))
})

test('an instant can be asked to carry the year, for the other end of a span that needs it', () => {
  assert.ok(dateTime(`${thisYear}-01-02T12:00:00`, true).includes(String(thisYear)))
})
