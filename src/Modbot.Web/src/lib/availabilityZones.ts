// Moving a person's week between time zones, and nothing that talks to the server or the page, so
// the Node test runner loads this file as it is (the split `calendarGrid.ts` and `nav.ts` use).
// Only `Intl`: no date library.
//
// A person's week is stored as hours of their OWN clock: Tuesday 18:00 is Tuesday 18:00 for them
// whatever their country does with its clocks (availability design §4). Whoever looks converts it
// for the dates they are looking at, and this is where. The grid a viewer sees is one real week
// -- seven dates, each with its own twenty-four hours in the viewer's zone -- so a week in which
// either zone changes its clocks shows that change on the days after it, and not before.
//
// The way across is by instant. Each hour of the viewer's grid is a real instant (two when the
// clocks go back and the hour happens twice, none when they go forward and it is skipped). The
// person's hour that goes in it is the one that STARTS in the half hour either side of it, which is
// the person's hour start rounded to the nearest viewer hour, a half rounding up. That keeps every
// hour a person marks as exactly one hour on the viewer's grid, a zone that sits on the half hour
// (Asia/Kolkata) included, instead of smearing it over two hours or dropping a lone one.

export type AvailabilityState = 'free' | 'ifNeeded'

/** One hour of a week: `day` is 0 for Monday to 6 for Sunday, `hour` is 0 to 23. */
export type AvailabilityCell = { day: number; hour: number; state: AvailabilityState }

/** A date on the calendar, with no time and no zone. `month` is 1 to 12. */
export type LocalDate = { year: number; month: number; day: number }

export const DAY_NAMES = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] as const

export const HOURS_IN_WEEK = 7 * 24

const MINUTE_MS = 60_000
const HOUR_MS = 60 * MINUTE_MS
const DAY_MS = 24 * HOUR_MS

// ── What a zone says the time is ────────────────────────────────────────────────────────────

const formatters = new Map<string, Intl.DateTimeFormat>()

function formatterFor(zone: string): Intl.DateTimeFormat {
  let formatter = formatters.get(zone)
  if (!formatter) {
    // `hourCycle` rather than `hour12: false`, which some engines answer with 24 at midnight.
    formatter = new Intl.DateTimeFormat('en-US', {
      timeZone: zone,
      hourCycle: 'h23',
      year: 'numeric',
      month: 'numeric',
      day: 'numeric',
      hour: 'numeric',
      minute: 'numeric',
      second: 'numeric',
    })
    formatters.set(zone, formatter)
  }
  return formatter
}

const offsets = new Map<string, number>()

/** How far ahead of UTC a zone's clocks are at an instant, in milliseconds. */
export function offsetAt(zone: string, instant: number): number {
  const key = `${zone}|${instant}`
  const known = offsets.get(key)
  if (known !== undefined) return known

  const parts = formatterFor(zone).formatToParts(new Date(instant))
  const part = (type: string) => Number(parts.find((p) => p.type === type)?.value ?? 0)
  const asUtc = Date.UTC(part('year'), part('month') - 1, part('day'), part('hour') % 24, part('minute'), part('second'))
  const offset = asUtc - Math.floor(instant / 1000) * 1000

  // A week of a dozen people asks a few thousand times; past this the oldest answers are let go.
  if (offsets.size > 20_000) offsets.clear()
  offsets.set(key, offset)
  return offset
}

/** What a zone's clocks say at an instant: the weekday (Monday 0), the hour, the minute and the date. */
export function localAt(zone: string, instant: number): { weekday: number; hour: number; minute: number; date: LocalDate } {
  const shifted = new Date(instant + offsetAt(zone, instant))
  return {
    weekday: (shifted.getUTCDay() + 6) % 7,
    hour: shifted.getUTCHours(),
    minute: shifted.getUTCMinutes(),
    date: { year: shifted.getUTCFullYear(), month: shifted.getUTCMonth() + 1, day: shifted.getUTCDate() },
  }
}

/** Whether the browser knows this zone by that name. */
export function isKnownZone(zone: string): boolean {
  try {
    formatterFor(zone)
    return true
  } catch {
    return false
  }
}

/** The browser's own zone, UTC when it will not say. */
export function browserZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
  } catch {
    return 'UTC'
  }
}

/**
 * Every zone to pick from: the browser's list, UTC, and any zone named in `include` that the list
 * leaves out (a zone the server saved under an older name), sorted.
 */
export function zoneChoices(include: readonly (string | null | undefined)[] = []): string[] {
  let known: string[]
  try {
    known = Intl.supportedValuesOf('timeZone')
  } catch {
    known = []
  }

  const all = new Set<string>(['UTC', ...known])
  for (const zone of include) if (zone) all.add(zone)
  return [...all].sort((a, b) => a.localeCompare(b))
}

// ── Dates ───────────────────────────────────────────────────────────────────────────────────

/** A date some calendar days later, or earlier for a negative number. */
export function addDays(date: LocalDate, days: number): LocalDate {
  const moved = new Date(Date.UTC(date.year, date.month - 1, date.day + days))
  return { year: moved.getUTCFullYear(), month: moved.getUTCMonth() + 1, day: moved.getUTCDate() }
}

/** The Monday of the week an instant falls in, by the zone's calendar. */
export function weekStartFor(instant: number, zone: string): LocalDate {
  const now = localAt(zone, instant)
  return addDays(now.date, -now.weekday)
}

/**
 * The instants at which a zone's clocks read a date and an hour: one most of the time, two when the
 * clocks went back and the hour happens twice, none when they went forward past it.
 */
export function instantsOf(date: LocalDate, hour: number, zone: string): number[] {
  const wanted = Date.UTC(date.year, date.month - 1, date.day, hour)
  const found: number[] = []

  // The offset a day before and a day after sees both sides of a change of clocks, if there is
  // one. Each gives an instant, which counts only if the clocks really read the hour at it.
  for (const probe of [wanted - DAY_MS, wanted + DAY_MS]) {
    const instant = wanted - offsetAt(zone, probe)
    if (offsetAt(zone, instant) === wanted - instant && !found.includes(instant)) found.push(instant)
  }

  return found.sort((a, b) => a - b)
}

// ── A week, seen from another zone ──────────────────────────────────────────────────────────

/**
 * The hours of a viewer's week: for each day of the week starting `weekStart` (a Monday by the
 * viewer's calendar) and each hour, in the order day by day, the instants at which the viewer's
 * clocks read it. Worked out once for a viewer and used for everyone they look at.
 */
export function viewerWeek(weekStart: LocalDate, viewerZone: string): number[][] {
  const grid: number[][] = []
  for (let day = 0; day < 7; day += 1) {
    const date = addDays(weekStart, day)
    for (let hour = 0; hour < 24; hour += 1) grid.push(instantsOf(date, hour, viewerZone))
  }
  return grid
}

/**
 * The hour of the person's week whose start is nearest an instant, a half rounding up: the one
 * that starts in the half hour either side of it. As an index, `day * 24 + hour`.
 */
function personHourNear(instant: number, personZone: string): number {
  const early = instant - 30 * MINUTE_MS
  const wait = (60 - localAt(personZone, early).minute) % 60
  const start = early + wait * MINUTE_MS
  const at = localAt(personZone, start)
  return at.weekday * 24 + at.hour
}

/**
 * A person's week as the viewer's grid shows it: for each of its hours, `free`, `ifNeeded` or null.
 * An hour the viewer's clocks happen twice is free if either of them is, free winning over if
 * needed; an hour they skip is null.
 */
export function inViewersWeek(
  cells: readonly AvailabilityCell[],
  personZone: string | null,
  viewer: number[][],
): (AvailabilityState | null)[] {
  if (!personZone || cells.length === 0) return viewer.map(() => null)

  const states = new Map<number, AvailabilityState>()
  for (const cell of cells) states.set(cell.day * 24 + cell.hour, cell.state)

  return viewer.map((instants) => {
    let state: AvailabilityState | null = null
    for (const instant of instants) {
      const there = states.get(personHourNear(instant, personZone))
      if (there === 'free') return 'free'
      if (there === 'ifNeeded') state = 'ifNeeded'
    }
    return state
  })
}

/** `09:00`. */
export function hourText(hour: number): string {
  return `${String(hour).padStart(2, '0')}:00`
}
