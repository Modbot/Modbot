import type { MemberCountPoint, MemberCountRange } from '@/lib/api'

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
export type TimeFormat = { locale?: string; timeZone?: string }

/**
 * An axis label for an instant, in the viewer's own clock.
 *
 * What is written depends on how long the whole axis is: the time of day across a day, the day
 * across weeks and months, the month across years. The same instant is written the same way at
 * every tick of one axis, so the eye reads a scale and not a list.
 */
export function timeLabel(ms: number, spanMs: number, format: TimeFormat = {}): string {
  const d = new Date(ms)

  if (spanMs <= 2 * DAY)
    return d.toLocaleTimeString(format.locale, { hour: '2-digit', minute: '2-digit', timeZone: format.timeZone })

  if (spanMs <= 400 * DAY)
    return d.toLocaleDateString(format.locale, { month: 'short', day: 'numeric', timeZone: format.timeZone })

  return d.toLocaleDateString(format.locale, { year: 'numeric', month: 'short', timeZone: format.timeZone })
}

/** The full time of one reading, for the tooltip. */
export function readingTime(ms: number, format: TimeFormat = {}): string {
  return new Date(ms).toLocaleString(format.locale, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    timeZone: format.timeZone,
  })
}
