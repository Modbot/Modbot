// The viewer's clock for every test in this file: London, which changes its clocks on 2026-03-29
// (01:00 to 02:00) and 2026-10-25 (02:00 back to 01:00). Set before any date is made; Node reads
// TZ again when it is assigned.
process.env.TZ = 'Europe/London'

import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { CalendarEventInput } from '../src/lib/calendar.ts'
import {
  addDays,
  addMonths,
  atMinutes,
  columnAt,
  daysBetween,
  daysTouched,
  drawnRange,
  eventTone,
  firstHour,
  isAllDay,
  layoutDay,
  monthDays,
  movedByDays,
  movedInput,
  movedTo,
  offsetToMinutes,
  resizedTo,
  segmentOn,
  shiftDate,
  shiftDays,
  shiftLocal,
  snapMinutes,
  startOfWeek,
  stepAnchor,
  viewDays,
  viewRange,
  viewTitle,
  wallClockIn,
} from '../src/lib/calendarGrid.ts'

const HOUR = 3_600_000

/** A local date in the viewer's (London) clock. */
const at = (y: number, m: number, d: number, h = 0, min = 0) => new Date(y, m - 1, d, h, min)

/** Formatting output with its thin and narrow spaces made plain. */
const plainSpaces = (text: string) => text.replace(/\s/g, ' ')

// ── Snapping ──────────────────────────────────────────────────────────────────────────────

test('minutes snap to the nearest quarter hour, or down to the one a press landed in', () => {
  assert.equal(snapMinutes(7), 0)
  assert.equal(snapMinutes(8), 15)
  assert.equal(snapMinutes(52), 45)
  assert.equal(snapMinutes(53), 60)
  assert.equal(snapMinutes(14, 15, 'floor'), 0)
  assert.equal(snapMinutes(29, 15, 'floor'), 15)
  assert.equal(snapMinutes(-7), 0)
  assert.equal(Object.is(snapMinutes(-7), -0), false)
  assert.equal(snapMinutes(-8), -15)
  assert.equal(snapMinutes(40, 30), 30)
})

// ── Drag to time ──────────────────────────────────────────────────────────────────────────

test('a point in a day column is the snapped minute it stands for', () => {
  // A column 1440 px tall: a pixel a minute.
  assert.equal(offsetToMinutes(0, 1440), 0)
  assert.equal(offsetToMinutes(607, 1440), 600)
  assert.equal(offsetToMinutes(608, 1440), 615)
  assert.equal(offsetToMinutes(614, 1440, 15, 'floor'), 600)
  // Half as tall: two minutes a pixel.
  assert.equal(offsetToMinutes(300, 720), 600)
  // Above and below the column are held to the day.
  assert.equal(offsetToMinutes(-50, 1440), 0)
  assert.equal(offsetToMinutes(2000, 1440), 1440)
  assert.equal(offsetToMinutes(10, 0), 0)
})

test('a point across a week is the column it falls in', () => {
  assert.equal(columnAt(0, 700, 7), 0)
  assert.equal(columnAt(99, 700, 7), 0)
  assert.equal(columnAt(100, 700, 7), 1)
  assert.equal(columnAt(699, 700, 7), 6)
  assert.equal(columnAt(-20, 700, 7), 0)
  assert.equal(columnAt(900, 700, 7), 6)
})

test('a click on empty time draws one hour from the quarter it landed in', () => {
  const day = at(2026, 9, 27)
  const { start, end } = drawnRange(day, 622, 622)
  assert.deepEqual(start, at(2026, 9, 27, 10, 15))
  assert.deepEqual(end, at(2026, 9, 27, 11, 15))
})

test('a drag on empty time draws from where it started to where it let go, either way round', () => {
  const day = at(2026, 9, 27)
  assert.deepEqual(drawnRange(day, 600, 690), { start: at(2026, 9, 27, 10), end: at(2026, 9, 27, 11, 30) })
  assert.deepEqual(drawnRange(day, 690, 600), { start: at(2026, 9, 27, 10), end: at(2026, 9, 27, 11, 30) })
})

test('a click in the last hour of the day ends at midnight', () => {
  const day = at(2026, 9, 27)
  const { start, end } = drawnRange(day, 1430, 1430)
  assert.deepEqual(start, at(2026, 9, 27, 23, 45))
  assert.deepEqual(end, at(2026, 9, 28))
})

test('a dropped event keeps its length and lands at the minute it was dropped on', () => {
  const event = { start: at(2026, 9, 22, 20), end: at(2026, 9, 22, 22) }
  const moved = movedTo(event, at(2026, 9, 24), 19 * 60 + 30)
  assert.deepEqual(moved.start, at(2026, 9, 24, 19, 30))
  assert.deepEqual(moved.end, at(2026, 9, 24, 21, 30))
})

test('a dropped event cannot start past the last quarter of the day', () => {
  const event = { start: at(2026, 9, 22, 20), end: at(2026, 9, 22, 21) }
  assert.deepEqual(movedTo(event, at(2026, 9, 22), 1440).start, at(2026, 9, 22, 23, 45))
})

test('an event dropped across the night the clocks go back is still two real hours long', () => {
  const event = { start: at(2026, 10, 24, 20), end: at(2026, 10, 24, 22) }
  const moved = movedTo(event, at(2026, 10, 25), 30)

  // 00:30 BST, then two real hours: 02:30 BST does not happen that night, so the wall reads 01:30 GMT.
  assert.equal(moved.start.getHours(), 0)
  assert.equal(moved.start.getMinutes(), 30)
  assert.equal(moved.end.getTime() - moved.start.getTime(), 2 * HOUR)
  assert.equal(moved.end.getHours(), 1)
  assert.equal(moved.end.getMinutes(), 30)
})

test('dragging along a month keeps the time of day, across a clock change', () => {
  const event = { start: at(2026, 10, 23, 20), end: at(2026, 10, 23, 22) }
  const moved = movedByDays(event, 3)
  assert.deepEqual(moved.start, at(2026, 10, 26, 20))
  assert.deepEqual(moved.end, at(2026, 10, 26, 22))
})

test('dragging the bottom edge changes the end, never to before the start plus a quarter', () => {
  const event = { start: at(2026, 9, 22, 20), end: at(2026, 9, 22, 21) }
  assert.deepEqual(resizedTo(event, at(2026, 9, 22), 22 * 60 + 30).end, at(2026, 9, 22, 22, 30))
  assert.deepEqual(resizedTo(event, at(2026, 9, 22), 19 * 60).end, at(2026, 9, 22, 20, 15))
  // Past midnight, onto the next day's column.
  assert.deepEqual(resizedTo(event, at(2026, 9, 23), 60).end, at(2026, 9, 23, 1))
})

// ── Weeks and months ──────────────────────────────────────────────────────────────────────

test('a week starts on the Monday on or before the day', () => {
  assert.deepEqual(startOfWeek(at(2026, 9, 27, 15)), at(2026, 9, 21))
  assert.deepEqual(startOfWeek(at(2026, 9, 21, 0, 5)), at(2026, 9, 21))
  assert.deepEqual(startOfWeek(at(2026, 1, 1)), at(2025, 12, 29))
})

test('the week the clocks go back is seven midnights, ending on the 25th', () => {
  const days = viewDays('week', at(2026, 10, 22))
  assert.equal(days.length, 7)
  assert.deepEqual(days[0], at(2026, 10, 19))
  assert.deepEqual(days[6], at(2026, 10, 25))
  assert.ok(days.every((d) => d.getHours() === 0 && d.getMinutes() === 0))

  const { from, to } = viewRange('week', at(2026, 10, 22))
  assert.deepEqual(from, at(2026, 10, 19))
  assert.deepEqual(to, at(2026, 10, 26))
  // One hour longer than seven times twenty-four.
  assert.equal(to.getTime() - from.getTime(), (7 * 24 + 1) * HOUR)
})

test('the week the clocks go forward is seven midnights too', () => {
  const days = viewDays('week', at(2026, 3, 29, 12))
  assert.deepEqual(days[0], at(2026, 3, 23))
  assert.deepEqual(days[6], at(2026, 3, 29))
  assert.ok(days.every((d) => d.getHours() === 0))
  assert.deepEqual(addDays(at(2026, 3, 29), 1), at(2026, 3, 30))
  assert.equal(daysBetween(at(2026, 3, 28), at(2026, 3, 30, 23, 59)), 2)
  assert.equal(daysBetween(at(2026, 10, 26), at(2026, 10, 24)), -2)
})

test('a month is six weeks from the Monday on or before its first', () => {
  const september = monthDays(at(2026, 9, 15))
  assert.equal(september.length, 42)
  assert.deepEqual(september[0], at(2026, 8, 31))
  assert.deepEqual(september[41], at(2026, 10, 11))

  // February 2026 starts on a Sunday.
  const february = monthDays(at(2026, 2, 10))
  assert.deepEqual(february[0], at(2026, 1, 26))

  // March 2026 holds the spring change; every cell is still a midnight.
  assert.ok(monthDays(at(2026, 3, 1)).every((d) => d.getHours() === 0))
})

test('a month step keeps the day, held to the shorter month', () => {
  assert.deepEqual(addMonths(at(2026, 1, 31), 1), at(2026, 2, 28))
  assert.deepEqual(addMonths(at(2026, 3, 31), -1), at(2026, 2, 28))
  assert.deepEqual(addMonths(at(2026, 12, 15), 1), at(2027, 1, 15))
  assert.deepEqual(stepAnchor('month', at(2026, 10, 31), 1), at(2026, 11, 30))
  assert.deepEqual(stepAnchor('week', at(2026, 10, 22), 1), at(2026, 10, 29))
  assert.deepEqual(stepAnchor('day', at(2026, 3, 29), -1), at(2026, 3, 28))
})

test('the title names the month, the week or the day the way Google does', () => {
  assert.equal(viewTitle('month', at(2026, 9, 27), 'en-US'), 'September 2026')
  assert.equal(plainSpaces(viewTitle('week', at(2026, 9, 27), 'en-US')), 'Sep 21 – 27, 2026')
  assert.equal(plainSpaces(viewTitle('week', at(2026, 9, 30), 'en-US')), 'Sep 28 – Oct 4, 2026')
  assert.equal(plainSpaces(viewTitle('week', at(2026, 12, 30), 'en-US')), 'Dec 28, 2026 – Jan 3, 2027')
  assert.equal(viewTitle('day', at(2026, 9, 27), 'en-US'), 'Sunday, Sep 27, 2026')
})

// ── Laying out a day ──────────────────────────────────────────────────────────────────────

test('events that overlap sit side by side, and a later one takes a column that has come free', () => {
  const placed = layoutDay([
    { key: 'a', start: 0, end: 60 },
    { key: 'b', start: 30, end: 90 },
    { key: 'c', start: 60, end: 120 },
    { key: 'd', start: 200, end: 260 },
  ])
  const by = Object.fromEntries(placed.map((p) => [p.key, p]))

  assert.deepEqual(by.a, { key: 'a', column: 0, span: 1, columns: 2 })
  assert.deepEqual(by.b, { key: 'b', column: 1, span: 1, columns: 2 })
  assert.deepEqual(by.c, { key: 'c', column: 0, span: 1, columns: 2 })
  // Not touching anything: the whole width.
  assert.deepEqual(by.d, { key: 'd', column: 0, span: 1, columns: 1 })
})

test('an event widens over columns nothing it overlaps is using', () => {
  const placed = layoutDay([
    { key: 'long', start: 0, end: 120 },
    { key: 'b', start: 0, end: 30 },
    { key: 'c', start: 0, end: 30 },
    { key: 'd', start: 30, end: 60 },
  ])
  const by = Object.fromEntries(placed.map((p) => [p.key, p]))

  assert.equal(by.long.column, 0)
  assert.equal(by.b.column, 1)
  assert.equal(by.c.column, 2)
  assert.equal(by.d.column, 1)
  assert.equal(by.d.span, 2)
  assert.ok(placed.every((p) => p.columns === 3))
})

test('the longer of two events starting together takes the left column', () => {
  const placed = layoutDay([
    { key: 'short', start: 60, end: 90 },
    { key: 'long', start: 60, end: 180 },
  ])
  assert.equal(placed.find((p) => p.key === 'long')?.column, 0)
  assert.equal(placed.find((p) => p.key === 'short')?.column, 1)
})

test('short events drawn taller than their time are laid out by the height they are drawn', () => {
  const items = [
    { key: 'a', start: 0, end: 15 },
    { key: 'b', start: 15, end: 30 },
  ]
  assert.ok(layoutDay(items).every((p) => p.columns === 1))
  assert.ok(layoutDay(items, 30).every((p) => p.columns === 2))
})

test('an event past midnight is drawn in a piece on each day', () => {
  const event = { start: at(2026, 9, 26, 22), end: at(2026, 9, 27, 2) }
  assert.deepEqual(segmentOn(event, at(2026, 9, 26)), { start: 22 * 60, end: 1440 })
  assert.deepEqual(segmentOn(event, at(2026, 9, 27)), { start: 0, end: 120 })
  assert.equal(segmentOn(event, at(2026, 9, 28)), null)
})

test('a day or longer goes in the all-day row; an evening that ends at midnight touches one day', () => {
  assert.equal(isAllDay({ start: at(2026, 9, 26, 20), end: at(2026, 9, 27, 20) }), true)
  assert.equal(isAllDay({ start: at(2026, 9, 26, 20), end: at(2026, 9, 27, 19) }), false)
  assert.deepEqual(daysTouched({ start: at(2026, 9, 27, 20), end: at(2026, 9, 28) }), [at(2026, 9, 27)])
  assert.deepEqual(daysTouched({ start: at(2026, 9, 27, 20), end: at(2026, 9, 29, 1) }), [
    at(2026, 9, 27),
    at(2026, 9, 28),
    at(2026, 9, 29),
  ])
})

test('the grid opens an hour before the first event, or at 8 AM', () => {
  assert.equal(firstHour([]), 8)
  assert.equal(firstHour([at(2026, 9, 27, 20), at(2026, 9, 28, 18, 30)]), 17)
  assert.equal(firstHour([at(2026, 9, 27, 0, 15)]), 0)
})

test('the colour follows what a moderator acts on', () => {
  assert.equal(eventTone({ state: 'scheduled', places: [{ state: 'published' }] }), 'scheduled')
  assert.equal(eventTone({ state: 'scheduled', places: [{ state: 'failed' }] }), 'failed')
  assert.equal(eventTone({ state: 'open', places: [] }), 'open')
  assert.equal(eventTone({ state: 'draft', places: [] }), 'draft')
  assert.equal(eventTone({ state: 'finished', places: [{ state: 'failed' }] }), 'finished')
})

// ── Saving a drag in the event's own zone ─────────────────────────────────────────────────

test('an instant reads as the wall clock of the zone it is asked for', () => {
  const instant = new Date('2026-09-20T19:00:00Z')
  assert.equal(wallClockIn(instant, 'Europe/London'), '2026-09-20T20:00')
  assert.equal(wallClockIn(instant, 'America/New_York'), '2026-09-20T15:00')
  assert.equal(wallClockIn(new Date('2026-09-20T23:30:00Z'), 'Asia/Tokyo'), '2026-09-21T08:30')
})

test('wall-clock texts move by wall-clock minutes, across a month and a year', () => {
  assert.equal(shiftLocal('2026-09-30T23:00', 90), '2026-10-01T00:30')
  assert.equal(shiftLocal('2026-12-31T20:00', 24 * 60), '2027-01-01T20:00')
  assert.equal(shiftDate('2026-02-28', 1), '2026-03-01')
  assert.deepEqual(shiftDays(['MO', 'WE'], 1), ['TU', 'TH'])
  // Monday a day earlier is Sunday, and the days come back in Monday-first order.
  assert.deepEqual(shiftDays(['MO', 'WE'], -1), ['TU', 'SU'])
  assert.deepEqual(shiftDays(['SU'], 8), ['MO'])
})

function input(over: Partial<CalendarEventInput>): CalendarEventInput {
  return {
    title: 'Movie night',
    description: 'Films',
    startsAt: '2026-09-30T20:00',
    endsAt: '2026-09-30T22:00',
    timeZone: 'Europe/London',
    repeat: 'none',
    repeatDays: [],
    repeatUntil: null,
    worldId: null,
    accessType: 'members',
    region: 'eu',
    imageUrl: null,
    vrChatImageId: null,
    category: 'hangout',
    languages: [],
    platforms: [],
    tags: [],
    visibility: 'group',
    notifyMembers: false,
    publishToVRChat: true,
    publishToDiscord: false,
    postToChannel: false,
    channelId: null,
    autoOpen: false,
    openMinutesBefore: 10,
    draft: false,
    ...over,
  }
}

test('a one-off event lands where it was dropped, written in its own zone', () => {
  // Saved in New York; the viewer is in London, five hours ahead.
  const base = input({ timeZone: 'America/New_York' })
  const was = { start: new Date('2026-10-01T00:00:00Z'), end: new Date('2026-10-01T02:00:00Z') }
  const now = { start: new Date(was.start.getTime() + 25 * HOUR), end: new Date(was.end.getTime() + 25 * HOUR) }

  const moved = movedInput(base, was, now)
  assert.equal(moved.startsAt, '2026-10-01T21:00')
  assert.equal(moved.endsAt, '2026-10-01T23:00')
  assert.equal(moved.timeZone, 'America/New_York')
  assert.equal(moved.title, 'Movie night')
  assert.equal(moved.draft, false)
})

test('a draft stays a draft when it is dragged', () => {
  const base = input({ draft: true })
  const was = { start: new Date('2026-09-30T19:00:00Z'), end: new Date('2026-09-30T21:00:00Z') }
  const now = { start: new Date('2026-09-30T20:00:00Z'), end: new Date('2026-09-30T22:00:00Z') }
  assert.equal(movedInput(base, was, now).draft, true)
})

test('dragging the bottom edge moves only the end', () => {
  const base = input({})
  const was = { start: new Date('2026-09-30T19:00:00Z'), end: new Date('2026-09-30T21:00:00Z') }
  const now = { start: was.start, end: new Date('2026-09-30T21:45:00Z') }

  const moved = movedInput(base, was, now)
  assert.equal(moved.startsAt, '2026-09-30T20:00')
  assert.equal(moved.endsAt, '2026-09-30T22:45')
})

test('a weekly event moved a day later moves every week, its days and its last date with it', () => {
  // Mondays and Wednesdays from Monday 2026-09-21, until the end of the year. The Wednesday
  // 2026-09-30 occurrence is dragged to Thursday, an hour later.
  const base = input({
    startsAt: '2026-09-21T20:00',
    endsAt: '2026-09-21T22:00',
    repeat: 'weekly',
    repeatDays: ['MO', 'WE'],
    repeatUntil: '2026-12-31',
  })
  const was = { start: new Date('2026-09-30T19:00:00Z'), end: new Date('2026-09-30T21:00:00Z') }
  const now = { start: new Date('2026-10-01T20:00:00Z'), end: new Date('2026-10-01T22:00:00Z') }

  const moved = movedInput(base, was, now)
  assert.equal(moved.startsAt, '2026-09-22T21:00')
  assert.equal(moved.endsAt, '2026-09-22T23:00')
  assert.deepEqual(moved.repeatDays, ['TU', 'TH'])
  assert.equal(moved.repeatUntil, '2027-01-01')
})

test('a weekly event is moved by its own wall clock after the clocks change', () => {
  // Weekly in London from Monday 2026-10-19 at 20:00 BST. The 2026-10-26 occurrence is after the
  // clocks went back (20:00 GMT) and is dragged an hour later: the series starts at 21:00 on the
  // 19th, not at 22:00, even though the two dates are a different distance from UTC.
  const base = input({ startsAt: '2026-10-19T20:00', endsAt: '2026-10-19T22:00', repeat: 'weekly', repeatDays: ['MO'] })
  const was = { start: new Date('2026-10-26T20:00:00Z'), end: new Date('2026-10-26T22:00:00Z') }
  const now = { start: new Date('2026-10-26T21:00:00Z'), end: new Date('2026-10-26T23:00:00Z') }

  const moved = movedInput(base, was, now)
  assert.equal(moved.startsAt, '2026-10-19T21:00')
  assert.equal(moved.endsAt, '2026-10-19T23:00')
  assert.deepEqual(moved.repeatDays, ['MO'])
})

test('a monthly event moved a day earlier moves its last date too, across a month boundary', () => {
  const base = input({
    startsAt: '2026-10-01T20:00',
    endsAt: '2026-10-01T22:00',
    repeat: 'monthly',
    repeatUntil: '2027-03-01',
  })
  const was = { start: new Date('2026-11-01T20:00:00Z'), end: new Date('2026-11-01T22:00:00Z') }
  const now = { start: new Date('2026-10-31T20:00:00Z'), end: new Date('2026-10-31T22:00:00Z') }

  const moved = movedInput(base, was, now)
  assert.equal(moved.startsAt, '2026-09-30T20:00')
  assert.equal(moved.repeatUntil, '2027-02-28')
  assert.deepEqual(moved.repeatDays, [])
})

test('a point in the grid and back again is the same minute on the same day', () => {
  const day = at(2026, 10, 25)
  const minutes = offsetToMinutes(1234, 1440)
  const when = atMinutes(day, minutes)
  assert.equal(when.getHours() * 60 + when.getMinutes(), minutes)
})
