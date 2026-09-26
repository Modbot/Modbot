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

/** `want` evenly spaced instants from `from` to `to`, both included, for the axis. */
export function timeTicks(from: number, to: number, want = 6): number[] {
  if (!(to > from)) return [from]

  const n = Math.max(2, want)
  const out: number[] = []
  for (let i = 0; i < n; i++) out.push(Math.round(from + ((to - from) * i) / (n - 1)))
  return out
}

const DAY = 86_400_000

/** Locale and zone, so a test can pin them; the page leaves both to the viewer's browser. */
/** `now` is the instant "this year" is taken from, for the tests; the browser's clock when left out. */
export type TimeFormat = { locale?: string; timeZone?: string; now?: number }

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
