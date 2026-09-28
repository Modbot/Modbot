// The date maths, the snapping and the overlap layout the calendar's views are drawn from -- and
// nothing that talks to the server or the page, so the Node test runner can load this file as it
// is. The same split `giveawayRules.ts` and `nav.ts` use, and for the same reason.
//
// Two clocks meet here. The grid is drawn in the viewer's own time (the browser's zone), because
// that is the day and hour a moderator reads off the screen. An event is saved in its own zone,
// because that is what its repeats are counted in. A drag is worked out in the first and written
// in the second: `movedInput` is where one becomes the other.
import type { CalendarEventInput } from './calendar.ts'

export type CalendarViewName = 'day' | 'week' | 'month' | 'schedule'

export const MINUTES_PER_DAY = 24 * 60

/** Every drag and every drawn event snaps to this many minutes. */
export const SNAP_MINUTES = 15

/** What a single click on empty time makes. */
export const CLICK_LENGTH_MINUTES = 60

/** How many days the Schedule view lists. The server answers at most 62 at a time. */
export const SCHEDULE_DAYS = 60

const MINUTE_MS = 60_000

// ── Days, in the viewer's own time ──────────────────────────────────────────────────────────
//
// Every step is by calendar day, never by 24 hours: a day in which the clocks go back is 25 hours
// long, and adding 24 hours to its midnight lands at 23:00 on the same day.

/** Midnight at the start of a date. */
export function startOfDay(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate())
}

/** The same wall-clock time, some calendar days later (or earlier). */
export function addDays(date: Date, days: number): Date {
  const next = new Date(date)
  next.setDate(next.getDate() + days)
  return next
}

/** The first of a month, at midnight. */
export function startOfMonth(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), 1)
}

/**
 * The same day of the month some months later, held to the month's last day: Jan 31 plus one
 * month is Feb 28, not Mar 3.
 */
export function addMonths(date: Date, months: number): Date {
  const first = new Date(date.getFullYear(), date.getMonth() + months, 1)
  const last = new Date(first.getFullYear(), first.getMonth() + 1, 0).getDate()
  return new Date(first.getFullYear(), first.getMonth(), Math.min(date.getDate(), last))
}

/** The Monday on or before a date, at midnight. */
export function startOfWeek(date: Date): Date {
  const day = startOfDay(date)
  return addDays(day, -((day.getDay() + 6) % 7))
}

export function sameDay(a: Date, b: Date): boolean {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()
}

/** Whole calendar days from `a`'s date to `b`'s, whatever the clocks did in between. */
export function daysBetween(a: Date, b: Date): number {
  const from = Date.UTC(a.getFullYear(), a.getMonth(), a.getDate())
  const to = Date.UTC(b.getFullYear(), b.getMonth(), b.getDate())
  return Math.round((to - from) / (MINUTES_PER_DAY * MINUTE_MS))
}

/** Minutes since midnight on the wall clock: 20:30 is 1230, on any day, DST or not. */
export function minutesIntoDay(date: Date): number {
  return date.getHours() * 60 + date.getMinutes()
}

/** A day at so many wall-clock minutes past its midnight. 1440 is the next midnight. */
export function atMinutes(day: Date, minutes: number): Date {
  return new Date(day.getFullYear(), day.getMonth(), day.getDate(), 0, minutes)
}

/** The six weeks a month view shows, Monday first: always 42 days, so the grid never jumps. */
export function monthDays(anchor: Date): Date[] {
  const first = startOfWeek(startOfMonth(anchor))
  return Array.from({ length: 42 }, (_, i) => addDays(first, i))
}

/**
 * How many days the Week view shows on a phone held upright. Seven columns there are about 38 px
 * each: the day names run into each other and a title shows one letter. Three leave room for a
 * title, and start on the day in view rather than on a Monday, so Today shows today and the next
 * two (mobile review 2026-09-28, #9).
 */
export const PHONE_WEEK_LENGTH = 3

/** The Week view's first day: its Monday for a whole week, the day itself for a shorter one. */
function firstOfWeek(anchor: Date, weekLength: number): Date {
  return weekLength === 7 ? startOfWeek(anchor) : startOfDay(anchor)
}

/**
 * The days a view shows as columns (day, week) or cells (month); the Schedule's first day.
 * `weekLength` is 7, or {@link PHONE_WEEK_LENGTH} on a phone.
 */
export function viewDays(view: CalendarViewName, anchor: Date, weekLength = 7): Date[] {
  if (view === 'day') return [startOfDay(anchor)]
  if (view === 'week') {
    const first = firstOfWeek(anchor, weekLength)
    return Array.from({ length: weekLength }, (_, i) => addDays(first, i))
  }
  if (view === 'month') return monthDays(anchor)
  return [startOfDay(anchor)]
}

/** The span of time to ask the server for: from the first day's midnight to the last day's end. */
export function viewRange(view: CalendarViewName, anchor: Date, weekLength = 7): { from: Date; to: Date } {
  if (view === 'schedule') {
    const from = startOfDay(anchor)
    return { from, to: addDays(from, SCHEDULE_DAYS) }
  }

  const days = viewDays(view, anchor, weekLength)
  return { from: days[0], to: addDays(days[days.length - 1], 1) }
}

/** Where the previous and next buttons (and `j`, `k`) go from here. */
export function stepAnchor(view: CalendarViewName, anchor: Date, direction: 1 | -1, weekLength = 7): Date {
  if (view === 'day') return addDays(anchor, direction)
  if (view === 'week') return addDays(anchor, weekLength * direction)
  if (view === 'month') return addMonths(anchor, direction)
  return addDays(anchor, 7 * direction)
}

// ── Titles ──────────────────────────────────────────────────────────────────────────────────

/**
 * The heading over a view, as Google words it: "September 2026" for a month, "Sep 21 – 27, 2026"
 * for a week inside one month, "Sep 28 – Oct 4, 2026" across two, "Dec 28, 2026 – Jan 3, 2027"
 * across a new year, and "Sunday, Sep 27, 2026" for one day. `locale` is left out on the page, so
 * the viewer's own language writes it; the tests pass one.
 */
export function viewTitle(view: CalendarViewName, anchor: Date, locale?: string, weekLength = 7): string {
  if (view === 'month') return new Intl.DateTimeFormat(locale, { month: 'long', year: 'numeric' }).format(anchor)

  if (view === 'day')
    return new Intl.DateTimeFormat(locale, { weekday: 'long', month: 'short', day: 'numeric', year: 'numeric' }).format(anchor)

  const from = view === 'week' ? firstOfWeek(anchor, weekLength) : startOfDay(anchor)
  const to = addDays(from, (view === 'week' ? weekLength : SCHEDULE_DAYS) - 1)
  return new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric', year: 'numeric' }).formatRange(from, to)
}

/**
 * An hour down the side of the grid, in the viewer's clock: "8 AM" where the clock has AM and PM,
 * "08:00" where it runs to 23.
 */
export function hourLabel(hour: number, locale?: string): string {
  const at = new Date(2026, 0, 5, hour)
  const cycle = new Intl.DateTimeFormat(locale, { hour: 'numeric' }).resolvedOptions().hourCycle
  if (cycle === 'h23' || cycle === 'h24')
    return new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(at)
  return new Intl.DateTimeFormat(locale, { hour: 'numeric' }).format(at)
}

// ── Snapping and the pointer ────────────────────────────────────────────────────────────────

/** Minutes held to the snap step. `floor` for where a press lands, `round` for how far a drag went. */
export function snapMinutes(minutes: number, step: number = SNAP_MINUTES, mode: 'round' | 'floor' = 'round'): number {
  const steps = mode === 'floor' ? Math.floor(minutes / step) : Math.round(minutes / step)
  // `+ 0` turns the -0 that rounding a small negative gives into 0.
  return steps * step + 0
}

export function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value))
}

/**
 * The wall-clock minute a point in a day column stands for, snapped. `offset` is pixels from the
 * top of the column, `height` the column's whole height (twenty-four hours of it).
 */
export function offsetToMinutes(
  offset: number,
  height: number,
  step: number = SNAP_MINUTES,
  mode: 'round' | 'floor' = 'round',
): number {
  if (height <= 0) return 0
  return clamp(snapMinutes((offset / height) * MINUTES_PER_DAY, step, mode), 0, MINUTES_PER_DAY)
}

/** Which of `count` equal columns a point falls in, held to the columns there are. */
export function columnAt(offset: number, width: number, count: number): number {
  if (width <= 0 || count <= 0) return 0
  return clamp(Math.floor((offset / width) * count), 0, count - 1)
}

/**
 * The time drawn by pressing on empty time at `from` minutes and letting go at `to`, on one day.
 * Either way round; a press with no drag (or less than one step) makes a one-hour event from the
 * step it landed in.
 */
export function drawnRange(day: Date, from: number, to: number, step: number = SNAP_MINUTES): { start: Date; end: Date } {
  const low = snapMinutes(Math.min(from, to), step, 'floor')
  let high = snapMinutes(Math.max(from, to), step, 'round')

  if (high - low < step) high = low + CLICK_LENGTH_MINUTES
  if (high > MINUTES_PER_DAY) high = MINUTES_PER_DAY

  const start = Math.min(low, MINUTES_PER_DAY - step)
  return { start: atMinutes(day, start), end: atMinutes(day, Math.max(high, start + step)) }
}

/**
 * Where an event lands when it is dropped on `day` with its top at `minutes`: the same length as
 * before, counted in real time, so a two-hour event is still two hours when it is dropped across
 * the night the clocks change.
 */
export function movedTo(
  event: { start: Date; end: Date },
  day: Date,
  minutes: number,
): { start: Date; end: Date } {
  const top = clamp(minutes, 0, MINUTES_PER_DAY - SNAP_MINUTES)
  const start = atMinutes(day, top)
  return { start, end: new Date(start.getTime() + (event.end.getTime() - event.start.getTime())) }
}

/**
 * Where an event lands when it is dragged some days along a month grid or the all-day row: the
 * same time of day, on another date.
 */
export function movedByDays(event: { start: Date; end: Date }, days: number): { start: Date; end: Date } {
  const start = addDays(event.start, days)
  return { start, end: new Date(start.getTime() + (event.end.getTime() - event.start.getTime())) }
}

/**
 * A new end when the bottom edge is dragged to `minutes` on `day`: never before the start plus
 * one step.
 */
export function resizedTo(event: { start: Date; end: Date }, day: Date, minutes: number, step: number = SNAP_MINUTES): { start: Date; end: Date } {
  const end = atMinutes(day, minutes)
  const earliest = new Date(event.start.getTime() + step * MINUTE_MS)
  return { start: event.start, end: end.getTime() < earliest.getTime() ? earliest : end }
}

// ── Laying out a day ────────────────────────────────────────────────────────────────────────

export type DayItem = { key: string; start: number; end: number }

export type PlacedItem = {
  key: string
  /** Which column the item sits in, from the left. */
  column: number
  /** How many columns it spans: it widens to the right over columns nothing else needs. */
  span: number
  /** How many columns its group of overlapping items is split into. */
  columns: number
}

/**
 * Side by side, the way Google lays out a day. Items that overlap, directly or through a chain of
 * others, form one group and share its width in equal columns; each item takes the first column
 * that is free when it starts, and then widens to the right over any columns nothing it overlaps
 * is using.
 *
 * `minLength` is the shortest an item is drawn, in minutes. A fifteen-minute event is drawn a
 * whole control tall, and two short events that only touch in time would sit on top of each other
 * on screen if they were laid out by time alone.
 */
export function layoutDay(items: DayItem[], minLength: number = 0): PlacedItem[] {
  const sorted = [...items]
    .map((item) => ({ ...item, bottom: Math.max(item.end, item.start + minLength) }))
    .sort((a, b) => a.start - b.start || b.bottom - a.bottom || a.key.localeCompare(b.key))

  const placed: PlacedItem[] = []
  let group: (typeof sorted[number] & { column: number })[] = []
  let groupEnd = -Infinity

  const close = () => {
    const columns = group.reduce((most, item) => Math.max(most, item.column + 1), 0)
    for (const item of group) {
      let span = 1
      while (
        item.column + span < columns &&
        !group.some((other) => other.column === item.column + span && other.start < item.bottom && item.start < other.bottom)
      )
        span++
      placed.push({ key: item.key, column: item.column, span, columns })
    }
    group = []
    groupEnd = -Infinity
  }

  for (const item of sorted) {
    if (item.start >= groupEnd && group.length > 0) close()

    // The first column whose last item has ended by the time this one starts.
    let column = 0
    while (group.some((other) => other.column === column && other.bottom > item.start)) column++

    group.push({ ...item, column })
    groupEnd = Math.max(groupEnd, item.bottom)
  }

  if (group.length > 0) close()

  return placed
}

/**
 * The part of an event that falls on one day, in wall-clock minutes from that day's midnight, or
 * null when it does not touch the day. An event past midnight is drawn in two pieces, as Google
 * draws it.
 */
export function segmentOn(event: { start: Date; end: Date }, day: Date): { start: number; end: number } | null {
  const dayStart = startOfDay(day)
  const dayEnd = addDays(dayStart, 1)
  if (event.end.getTime() <= dayStart.getTime() || event.start.getTime() >= dayEnd.getTime()) return null

  const start = event.start.getTime() <= dayStart.getTime() ? 0 : minutesIntoDay(event.start)
  const end = event.end.getTime() >= dayEnd.getTime() ? MINUTES_PER_DAY : minutesIntoDay(event.end)
  return { start, end: Math.max(end, start) }
}

/** An event a day or longer goes in the all-day row rather than down the hours. */
export function isAllDay(event: { start: Date; end: Date }): boolean {
  return event.end.getTime() - event.start.getTime() >= MINUTES_PER_DAY * MINUTE_MS
}

/** The dates an event touches, from its start's date to its end's (an end at midnight is the day before). */
export function daysTouched(event: { start: Date; end: Date }): Date[] {
  const first = startOfDay(event.start)
  const lastInstant = new Date(Math.max(event.start.getTime(), event.end.getTime() - 1))
  const count = daysBetween(first, lastInstant) + 1
  return Array.from({ length: count }, (_, i) => addDays(first, i))
}

// ── Saving a drag, in the event's own time zone ─────────────────────────────────────────────

/** An instant as the wall clock in a time zone reads it: `2026-09-20T20:00`, what the form holds. */
export function wallClockIn(instant: Date, timeZone: string): string {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).formatToParts(instant)

  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value ?? '00'
  return `${part('year')}-${part('month')}-${part('day')}T${part('hour')}:${part('minute')}`
}

/** A wall-clock text as minutes on a clock that never changes, so two of them can be subtracted. */
function naiveMinutes(local: string): number {
  const [date, time = '00:00'] = local.split('T')
  const [y, m, d] = date.split('-').map(Number)
  const [h, min] = time.split(':').map(Number)
  return Date.UTC(y, m - 1, d, h, min) / MINUTE_MS
}

function naiveText(minutes: number): string {
  const at = new Date(minutes * MINUTE_MS)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${at.getUTCFullYear()}-${pad(at.getUTCMonth() + 1)}-${pad(at.getUTCDate())}T${pad(at.getUTCHours())}:${pad(at.getUTCMinutes())}`
}

/** A wall-clock text moved by so many wall-clock minutes. */
export function shiftLocal(local: string, minutes: number): string {
  return naiveText(naiveMinutes(local) + minutes)
}

const WEEK_DAYS = ['MO', 'TU', 'WE', 'TH', 'FR', 'SA', 'SU']

/** Repeat days moved along the week: MO and WE moved one day are TU and TH. */
export function shiftDays(days: string[], by: number): string[] {
  const moved = days
    .map((d) => WEEK_DAYS.indexOf(d))
    .filter((i) => i >= 0)
    .map((i) => WEEK_DAYS[(((i + by) % 7) + 7) % 7])
  return WEEK_DAYS.filter((d) => moved.includes(d))
}

/** A `yyyy-MM-dd` date moved by so many days. */
export function shiftDate(date: string, by: number): string {
  return shiftLocal(`${date}T00:00`, by * MINUTES_PER_DAY).slice(0, 10)
}

/**
 * The form input that moves an event from one time to another: what a drag saves, through the same
 * update the form's Save sends.
 *
 * `base` is the event as it is saved now (`inputFrom`), `was` the occurrence that was dragged and
 * `now` where it was dropped. The change is measured on the event's own wall clock and applied to
 * its first start and end, so:
 *
 * - an event that does not repeat lands exactly where it was dropped, in its own zone;
 * - a repeating event moves as a whole, every time by the same amount, because the calendar keeps
 *   one rule and no exceptions to it (calendar design §2). Its weekly days and last date move with
 *   it, so a Monday-and-Wednesday event dragged a day later becomes Tuesday and Thursday, and it
 *   happens as many times as before.
 */
export function movedInput(
  base: CalendarEventInput,
  was: { start: Date; end: Date },
  now: { start: Date; end: Date },
): CalendarEventInput {
  const zone = base.timeZone
  const startBy = naiveMinutes(wallClockIn(now.start, zone)) - naiveMinutes(wallClockIn(was.start, zone))
  const endBy = naiveMinutes(wallClockIn(now.end, zone)) - naiveMinutes(wallClockIn(was.end, zone))

  const startsAt = shiftLocal(base.startsAt, startBy)
  const endsAt = shiftLocal(base.endsAt, endBy)
  const dayShift = Math.round((naiveMinutes(startsAt.slice(0, 10) + 'T00:00') - naiveMinutes(base.startsAt.slice(0, 10) + 'T00:00')) / MINUTES_PER_DAY)

  return {
    ...base,
    startsAt,
    endsAt,
    repeatDays: base.repeat === 'weekly' ? shiftDays(base.repeatDays, dayShift) : base.repeatDays,
    repeatUntil: base.repeat !== 'none' && base.repeatUntil ? shiftDate(base.repeatUntil, dayShift) : base.repeatUntil,
  }
}

// ── Colour ──────────────────────────────────────────────────────────────────────────────────

export type EventTone = 'draft' | 'scheduled' | 'open' | 'finished' | 'failed'

/**
 * What an event's colour says: a place failed (red), it is open now (green), it is a draft
 * (outlined), it is over (faded), or it is simply planned (the accent). Publish state, which the
 * moderator acts on, rather than a colour per event, which nobody can read.
 */
export function eventTone(event: { state: string; places: { state: string }[] }): EventTone {
  if (event.places.some((p) => p.state === 'failed') && (event.state === 'scheduled' || event.state === 'open')) return 'failed'
  if (event.state === 'open') return 'open'
  if (event.state === 'draft') return 'draft'
  if (event.state === 'finished' || event.state === 'cancelled') return 'finished'
  return 'scheduled'
}

/** The hour a time grid opens scrolled to: an hour before the first event in view, or 8 AM. */
export function firstHour(starts: Date[]): number {
  if (starts.length === 0) return 8
  const earliest = Math.min(...starts.map(minutesIntoDay))
  return clamp(Math.floor(earliest / 60) - 1, 0, 23)
}
