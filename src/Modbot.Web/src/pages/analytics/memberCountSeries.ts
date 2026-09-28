import type { GroupMemberCountSeries, MemberCountPoint, MemberCountRange } from '@/lib/api'
import { breakAtBands, timeBands } from '../../components/charts/coverage.ts'

/**
 * The member count chart's plain functions: its ranges, its rows, and how a time is written on
 * the axis and in the tooltip. No components, so the tests can run them under node and the page
 * keeps its fast-refresh boundary.
 */

export const MEMBER_COUNT_RANGES: { range: MemberCountRange; label: string }[] = [
  { range: 'day', label: 'Day' },
  { range: 'week', label: 'Week' },
  { range: 'month', label: 'Month' },
  { range: 'all', label: 'All' },
]

/** One chart row: the reading's time as a number, because the axis is a number line. */
export type MemberCountRow = { at: number; members: number; online: number }

export function toRows(points: MemberCountPoint[]): MemberCountRow[] {
  return points
    .map((p) => ({ at: Date.parse(p.at), members: p.members, online: p.online }))
    .filter((r) => Number.isFinite(r.at))
    .sort((a, b) => a.at - b.at)
}

/**
 * One row the chart draws. Readings go under `members` and `online`, drawn solid; points carried
 * from group-info facts go under the `…Carried` keys, drawn dashed. A row where the two meet
 * carries both, so the dashed stretch runs into the solid one instead of stopping short of it.
 */
export type MemberCountChartRow = {
  at: number
  members: number | null
  online: number | null
  membersCarried: number | null
  onlineCarried: number | null
}

/**
 * The chart's rows, its missing-day bands, and how many real readings it holds.
 *
 * The server used to splice facts and readings into one line with nothing to tell them apart, so a
 * month drawn from a handful of facts looked as measured as a month of five-minute readings. Each
 * point now says which it is (`carried`), and the days with neither come as a list; the line
 * breaks across those and a striped band sits behind them.
 */
export function memberCountRows(
  series: Pick<GroupMemberCountSeries, 'points' | 'from' | 'to' | 'daysWithoutReadings'>,
): { rows: MemberCountChartRow[]; bands: { x1: number; x2: number }[]; readings: number } {
  const points = series.points
    .map((p) => ({ at: Date.parse(p.at), members: p.members, online: p.online, carried: p.carried === true }))
    .filter((p) => Number.isFinite(p.at))
    .sort((a, b) => a.at - b.at)

  const rows: MemberCountChartRow[] = points.map((p) =>
    p.carried
      ? { at: p.at, members: null, online: null, membersCarried: p.members, onlineCarried: p.online }
      : { at: p.at, members: p.members, online: p.online, membersCarried: null, onlineCarried: null },
  )

  // Where a carried stretch meets readings, the reading also closes the dashed stretch.
  points.forEach((p, i) => {
    const next = points[i + 1]
    if (!next || next.carried === p.carried) return
    const reading = p.carried ? i + 1 : i
    rows[reading].membersCarried = points[reading].members
    rows[reading].onlineCarried = points[reading].online
  })

  const bands = timeBands(series.daysWithoutReadings ?? [], Date.parse(series.from), Date.parse(series.to))
  const gap = { members: null, online: null, membersCarried: null, onlineCarried: null }

  return {
    rows: breakAtBands(rows, bands, gap),
    bands,
    readings: points.filter((p) => !p.carried).length,
  }
}

const MINUTE = 60_000
const HOUR = 3_600_000
const DAY = 86_400_000

/** Locale and zone, so a test can pin them; the page leaves both to the viewer's browser. */
/** `now` is the instant "this year" is taken from, for the tests; the browser's clock when left out. */
export type TimeFormat = { locale?: string; timeZone?: string; now?: number }

/**
 * The axis's ticks: about `want` of them, each on a boundary the label names — a whole minute or
 * hour across a day or two, a midnight across weeks and months, the first of a month across years.
 *
 * Ticks used to be `want` equal slices of the span. A week cut into five gaps puts one every 1.4
 * days, each label names whatever day its slice starts on, and so one day in seven never gets a
 * label and reads as missing. A tick on the boundary names exactly the day (or hour) it stands on.
 *
 * The step is the smallest one that gives no more than `want + 2` ticks, so a week shows all seven
 * days. Days are counted back from the last midnight, so the newest day always has its label.
 * Boundaries are the viewer's own, in `format.timeZone` when a test pins one.
 */
export function timeTicks(from: number, to: number, want = 6, format: TimeFormat = {}): number[] {
  if (!(to > from)) return [from]

  const span = to - from
  const most = Math.max(2, want) + 2
  const steps: { every: number; ticks: () => number[] }[] =
    span <= 2 * DAY
      ? [
          ...[1, 2, 5, 10, 15, 30].map((m) => ({ every: m * MINUTE, ticks: () => minuteTicks(from, to, m, format) })),
          ...[1, 2, 3, 4, 6, 12].map((h) => ({ every: h * HOUR, ticks: () => hourTicks(from, to, h, format) })),
        ]
      : span <= 400 * DAY
        ? [
            ...[1, 2, 7, 14].map((d) => ({ every: d * DAY, ticks: () => dayTicks(from, to, d, format) })),
            ...[1, 3].map((m) => ({ every: m * 30 * DAY, ticks: () => monthTicks(from, to, m, format) })),
          ]
        : [1, 3, 6, 12, 24, 60].map((m) => ({ every: m * 30 * DAY, ticks: () => monthTicks(from, to, m, format) }))

  for (const step of steps) {
    // Only build the steps that could fit; a minute step across two days would be thousands.
    if (span / step.every > most + 1) continue
    const ticks = step.ticks()
    if (ticks.length >= 2 && ticks.length <= most) return ticks
  }

  // An axis too short for two whole minutes: evenly spaced, both ends included.
  const n = Math.max(2, want)
  const out: number[] = []
  for (let i = 0; i < n; i++) out.push(Math.round(from + (span * i) / (n - 1)))
  return out
}

/** Every `step` minutes on the viewer's clock (minute 0, 15, 30, 45 for fifteen). */
function minuteTicks(from: number, to: number, step: number, format: TimeFormat): number[] {
  // Zone offsets are whole minutes, so a whole minute here is a whole minute on the viewer's clock.
  let t = Math.ceil(from / MINUTE) * MINUTE
  while (t <= to && localParts(t, format.timeZone).minute % step !== 0) t += MINUTE

  const out: number[] = []
  for (; t <= to; t += step * MINUTE) out.push(t)
  return out
}

/** Every `step` hours on the viewer's clock, on the hour (00:00, 06:00, 12:00, 18:00 for six). */
function hourTicks(from: number, to: number, step: number, format: TimeFormat): number[] {
  const first = localParts(from, format.timeZone)
  let t = from - first.minute * MINUTE - first.second * 1000 - (from % 1000)
  if (t < from) t += HOUR

  // One hour at a time, so a clock change still lands every tick on a whole hour.
  const out: number[] = []
  for (; t <= to; t += HOUR) {
    const p = localParts(t, format.timeZone)
    if (p.minute === 0 && p.hour % step === 0) out.push(t)
  }
  return out
}

/** Midnights `step` days apart, counted back from the last midnight at or before `to`. */
function dayTicks(from: number, to: number, step: number, format: TimeFormat): number[] {
  const last = localParts(to, format.timeZone)
  const out: number[] = []
  for (let back = 0; ; back += step) {
    const t = localMidnight(last.year, last.month, last.day - back, format.timeZone)
    if (t < from) break
    if (t <= to) out.push(t)
  }
  return out.reverse()
}

/** The first of every `step`th month, counted from January, from `from` to `to`. */
function monthTicks(from: number, to: number, step: number, format: TimeFormat): number[] {
  const first = localParts(from, format.timeZone)
  const out: number[] = []
  for (let month = first.month; ; month++) {
    const t = localMidnight(first.year, month, 1, format.timeZone)
    if (t > to) break
    if (t >= from && (month - 1) % step === 0) out.push(t)
  }
  return out
}

type LocalParts = { year: number; month: number; day: number; hour: number; minute: number; second: number }

const clocks = new Map<string, Intl.DateTimeFormat>()

/** An instant as the date and time on the viewer's clock (or the pinned zone's). Month is 1–12. */
function localParts(ms: number, timeZone: string | undefined): LocalParts {
  const key = timeZone ?? ''
  let clock = clocks.get(key)
  if (!clock) {
    clock = new Intl.DateTimeFormat('en-US', {
      timeZone,
      hourCycle: 'h23',
      year: 'numeric',
      month: 'numeric',
      day: 'numeric',
      hour: 'numeric',
      minute: 'numeric',
      second: 'numeric',
    })
    clocks.set(key, clock)
  }
  const part = (type: Intl.DateTimeFormatPartTypes) =>
    Number(clock.formatToParts(ms).find((p) => p.type === type)?.value ?? 0)
  return {
    year: part('year'),
    month: part('month'),
    day: part('day'),
    hour: part('hour'),
    minute: part('minute'),
    second: part('second'),
  }
}

/**
 * The instant of midnight on a date on the viewer's clock. Day and month may run past their ends
 * (day 0 is the last of the month before), as `Date.UTC` allows.
 */
function localMidnight(year: number, month: number, day: number, timeZone: string | undefined): number {
  const wall = Date.UTC(year, month - 1, day)
  const offset = (t: number) => {
    const p = localParts(t, timeZone)
    return Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second) - Math.floor(t / 1000) * 1000
  }
  // Twice, because the offset at the first guess can be the one from the other side of a clock change.
  const guess = wall - offset(wall)
  return wall - offset(guess)
}

/**
 * An axis label for an instant, in the viewer's own clock.
 *
 * What is written depends on how long the whole axis is: the time of day across a day, the day
 * across weeks and months, the month across years. The same instant is written the same way at
 * every tick of one axis, so the eye reads a scale and not a list.
 */
export function timeLabel(ms: number, spanMs: number, format: TimeFormat = {}): string {
  const d = new Date(ms)

  // The lint rule against hand-written dates is off for these three: an axis label changes its
  // grain with the axis's length, and takes the locale and time zone from its caller so the tests
  // can pin both. No shared helper does either.
  if (spanMs <= 2 * DAY)
    // oxlint-disable-next-line no-restricted-properties
    return d.toLocaleTimeString(format.locale, { hour: '2-digit', minute: '2-digit', timeZone: format.timeZone })

  if (spanMs <= 400 * DAY)
    // oxlint-disable-next-line no-restricted-properties
    return d.toLocaleDateString(format.locale, { month: 'short', day: 'numeric', timeZone: format.timeZone })

  // oxlint-disable-next-line no-restricted-properties
  return d.toLocaleDateString(format.locale, { year: 'numeric', month: 'short', timeZone: format.timeZone })
}

/**
 * The full time of one reading, for the tooltip. The year only when it is not this year, the rule
 * every date in the app follows (`needsYear` in lib/format): here the year is read in the caller's
 * time zone, so the tests can pin it.
 */
export function readingTime(ms: number, format: TimeFormat = {}): string {
  const year = (t: number) => new Date(t).toLocaleString('en-US', { year: 'numeric', timeZone: format.timeZone })
  const withYear = year(ms) !== year(format.now ?? Date.now())

  return new Date(ms).toLocaleString(format.locale, {
    year: withYear ? 'numeric' : undefined,
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    timeZone: format.timeZone,
  })
}
