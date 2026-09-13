/**
 * Formatting for axis ticks and tooltips. Plain functions, no components, so a module that
 * exports these keeps its fast-refresh boundary.
 */

export type DayPoint = { day: string; value: number }

/** "Jun 16" -- a day label short enough for an axis. Days are UTC, like the data. */
export function shortDay(day: string): string {
  return new Date(`${day}T00:00:00Z`).toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
  })
}

/** "16 Jun 2026" -- a day label for a tooltip or a caption. */
export function longDay(day: string): string {
  return new Date(`${day}T00:00:00Z`).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
  })
}

/** An instant, in the viewer's own clock, because that is the clock they will act in. */
export function dateTime(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}

/** 12,345 -> "12.3K"; 42 -> "42"; 3.14159 -> "3.1". Never more digits than the eye can use. */
export const compactNumber = (n: number): string =>
  Math.abs(n) >= 10_000
    ? `${(n / 1000).toFixed(n % 1000 === 0 ? 0 : 1)}K`
    : Number.isInteger(n)
      ? n.toLocaleString()
      : n.toFixed(1)

/** Minutes, said the way a person would say them. */
export function minutes(total: number): string {
  if (!Number.isFinite(total) || total < 0) return '—'
  if (total < 1) return 'under a minute'
  if (total < 60) return `${Math.round(total)} min`
  if (total < 60 * 48) return `${(total / 60).toFixed(total % 60 === 0 ? 0 : 1)} h`
  return `${(total / 1440).toFixed(1)} days`
}

/** A whole-number percentage, or a dash when the denominator is zero. */
export function percent(part: number, whole: number): string {
  if (whole <= 0) return '—'
  return `${Math.round((part / whole) * 100)}%`
}

/**
 * Fills in the days a sparse series does not carry a row for.
 *
 * Daily totals and group-info facts are both written only on days something happened, so the raw
 * series has holes. `carry` is right for a level -- a headcount stays what it was -- and `zero` is
 * right for a count: no bans recorded is zero bans, not an unknown.
 */
export function denseDays(from: string, to: string, points: DayPoint[], mode: 'zero' | 'carry'): DayPoint[] {
  const byDay = new Map(points.map((p) => [p.day, p.value]))
  const out: DayPoint[] = []

  const start = new Date(`${from}T00:00:00Z`)
  const end = new Date(`${to}T00:00:00Z`)

  let last = 0
  let started = mode === 'zero'

  for (let d = start; d <= end; d = new Date(d.getTime() + 86_400_000)) {
    const day = d.toISOString().slice(0, 10)
    const value = byDay.get(day)

    if (value !== undefined) {
      last = value
      started = true
    }

    // A carried series does not begin until it has been observed once. Starting it at zero would
    // draw a cliff from nothing up to the first real reading, which nothing in the data supports.
    if (started) out.push({ day, value: mode === 'carry' ? last : (value ?? 0) })
  }

  return out
}

/**
 * Merges several sparse day series into one row per day, keyed by series, for a multi-series
 * chart. Every series is made dense with the same mode so the rows line up.
 */
export function mergeDays(
  from: string,
  to: string,
  series: { key: string; points: DayPoint[] }[],
  mode: 'zero' | 'carry',
): Record<string, number | string>[] {
  const rows = new Map<string, Record<string, number | string>>()

  for (const s of series) {
    for (const p of denseDays(from, to, s.points, mode)) {
      const row = rows.get(p.day) ?? { day: p.day }
      row[s.key] = p.value
      rows.set(p.day, row)
    }
  }

  return [...rows.values()].sort((a, b) => String(a.day).localeCompare(String(b.day)))
}

/**
 * Which day labels to show on an axis, so long windows do not become a wall of text.
 * Returns roughly `want` evenly spaced days, always including the first and last.
 */
export function tickDays(days: string[], want = 6): string[] {
  if (days.length <= want) return days
  const step = Math.ceil((days.length - 1) / (want - 1))
  const out: string[] = []
  for (let i = 0; i < days.length; i += step) out.push(days[i])
  if (out[out.length - 1] !== days[days.length - 1]) out.push(days[days.length - 1])
  return out
}
