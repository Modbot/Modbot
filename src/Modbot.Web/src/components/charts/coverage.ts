/**
 * Which days a chart draws as missing, as zero or as not over yet, and the rows that carry those
 * marks. Plain functions, no components, so the tests run them under node and the chart files keep
 * their fast-refresh boundary.
 *
 * The server decides which days a source has nothing for (`daysWithout…` on each analytics
 * response) and which day is today; nothing here reads the browser's clock. What this adds is one
 * safety rule: a day that has a recorded value is never drawn as missing, whatever the list says.
 * Hiding a real number behind a "no data" band would be a worse lie than the one the band fixes.
 */

export type DayPoint = { day: string; value: number }

/** What the chart is told about its days. Both optional, so a chart with no coverage draws as before. */
export type DayMarks = {
  /** Days the source has nothing for. */
  missing?: readonly string[]
  /** The last day, when it is not over yet. */
  today?: string | null
}

export type DayMark = 'missing' | 'today' | undefined

/** A run of missing days, by position in the chart's list of days. */
export type DayRun = { first: number; last: number }

/** The key a carried stretch of a line is drawn under, beside the series' own key. */
export const carriedKey = (key: string): string => `${key}:carried`

const DAY_MS = 86_400_000

/** Every day from `from` to `to`, both included, as `yyyy-mm-dd`. UTC, like the data. */
export function daysBetween(from: string, to: string): string[] {
  const out: string[] = []
  const end = Date.parse(`${to}T00:00:00Z`)
  for (let t = Date.parse(`${from}T00:00:00Z`); t <= end; t += DAY_MS) out.push(new Date(t).toISOString().slice(0, 10))
  return out
}

/**
 * The days to draw as missing: listed by the server, and with nothing recorded on them.
 *
 * `recorded` is the days that carry a value. For a count that is a value above zero, because a
 * count's rows are only ever written for days something happened, and a row of zeros from a
 * query that fills every day says nothing about whether anyone was watching.
 */
export function missingSet(days: readonly string[], missing: readonly string[] | undefined, recorded: ReadonlySet<string>) {
  const listed = new Set(missing ?? [])
  return new Set(days.filter((d) => listed.has(d) && !recorded.has(d)))
}

/** Days on which any series has a value other than zero. */
export function daysWithValues(series: { points: DayPoint[] }[]): Set<string> {
  const out = new Set<string>()
  for (const s of series) for (const p of s.points) if (p.value !== 0) out.add(p.day)
  return out
}

/** Consecutive runs of missing days, for one band each rather than one per day. */
export function missingRuns(days: readonly string[], missing: ReadonlySet<string>): DayRun[] {
  const runs: DayRun[] = []
  days.forEach((day, i) => {
    if (!missing.has(day)) return
    const last = runs[runs.length - 1]
    if (last && last.last === i - 1) last.last = i
    else runs.push({ first: i, last: i })
  })
  return runs
}

export function markOf(day: string, missing: ReadonlySet<string>, today: string | null | undefined): DayMark {
  if (missing.has(day)) return 'missing'
  if (today && day === today) return 'today'
  return undefined
}

export type BarRow = Record<string, number | string | undefined> & { day: string; mark?: DayMark }

/**
 * One row per day for a column chart, every series filled with zero where it has no row.
 *
 * A missing day keeps its zeros, so stacked columns still stack, and carries `mark: 'missing'`;
 * the column's shape draws nothing for it. A day really at zero is drawn as a thin mark on the
 * baseline, so the two no longer look the same.
 */
export function barRows(
  from: string,
  to: string,
  series: { key: string; points: DayPoint[] }[],
  marks: DayMarks = {},
): { rows: BarRow[]; runs: DayRun[] } {
  const days = daysBetween(from, to)
  const missing = missingSet(days, marks.missing, daysWithValues(series))
  const byKey = series.map((s) => ({ key: s.key, values: new Map(s.points.map((p) => [p.day, p.value])) }))

  const rows = days.map((day) => {
    const row: BarRow = { day, mark: markOf(day, missing, marks.today) }
    for (const s of byKey) row[s.key] = s.values.get(day) ?? 0
    return row
  })

  return { rows, runs: missingRuns(days, missing) }
}

export type LineRow = Record<string, number | string | null | undefined> & { i: number; day: string; mark?: DayMark }

/**
 * One row per day for a line chart, with each day's position as `i` so the axis can be a number
 * line and a band can cover exactly one day.
 *
 * `zero` fills a day with no row with nought, as a count should be. `carry` fills it with the last
 * value seen, as a level should be, but under `carriedKey(key)` rather than `key`: the chart draws
 * that stretch dashed, because the value was carried over, not measured. The carried stretch
 * includes the measured days either side of it, so the dashes join the solid line rather than
 * floating beside it.
 *
 * A missing day is null in every key, so the line breaks there instead of dipping to nought or
 * running straight across a day nobody saw.
 */
export function lineRows(
  from: string,
  to: string,
  series: { key: string; points: DayPoint[] }[],
  mode: 'zero' | 'carry',
  marks: DayMarks = {},
): { rows: LineRow[]; runs: DayRun[] } {
  const days = daysBetween(from, to)
  const recorded =
    mode === 'carry' ? new Set(series.flatMap((s) => s.points.map((p) => p.day))) : daysWithValues(series)
  const missing = missingSet(days, marks.missing, recorded)

  const rows: LineRow[] = days.map((day, i) => ({ i, day, mark: markOf(day, missing, marks.today) }))

  for (const s of series) {
    const values = new Map(s.points.map((p) => [p.day, p.value]))
    const carried = carriedKey(s.key)
    let last: number | null = null

    days.forEach((day, i) => {
      const row = rows[i]
      const value = values.get(day)
      row[s.key] = null
      row[carried] = null

      if (missing.has(day)) return

      if (value !== undefined) {
        row[s.key] = value
        last = value
      } else if (mode === 'zero') {
        row[s.key] = 0
      } else if (last !== null) {
        row[carried] = last
      }
    })

    // Join each carried stretch to the measured days on either side of it. The carried days are
    // found first, so a measured day given a carried value here does not pass it on to the next.
    if (mode === 'carry') {
      const carriedDays = rows.flatMap((row, i) => (row[carried] === null ? [] : [i]))
      for (const i of carriedDays) {
        const before = rows[i - 1]
        const after = rows[i + 1]
        if (before && before[s.key] !== null) before[carried] = before[s.key]
        if (after && after[s.key] !== null) after[carried] = after[s.key]
      }
    }
  }

  return { rows, runs: missingRuns(days, missing) }
}

/**
 * Bands for missing days on a chart whose axis is time, not days: each run of consecutive days as
 * one span in milliseconds, cut to the chart's own window.
 */
export function timeBands(missing: readonly string[], fromMs: number, toMs: number): { x1: number; x2: number }[] {
  const starts = [...new Set(missing)]
    .map((d) => Date.parse(`${d}T00:00:00Z`))
    .filter((t) => Number.isFinite(t))
    .sort((a, b) => a - b)

  const bands: { x1: number; x2: number }[] = []
  for (const start of starts) {
    const last = bands[bands.length - 1]
    if (last && last.x2 === start) last.x2 = start + DAY_MS
    else bands.push({ x1: start, x2: start + DAY_MS })
  }

  return bands
    .map((b) => ({ x1: Math.max(b.x1, fromMs), x2: Math.min(b.x2, toMs) }))
    .filter((b) => b.x2 > b.x1)
}

/**
 * Breaks a line of readings at every band, so it does not run straight across days nothing was
 * read: a row of nulls at each band's start, and any row inside a band dropped.
 */
export function breakAtBands<T extends { at: number }>(
  rows: T[],
  bands: { x1: number; x2: number }[],
  gap: Omit<T, 'at'>,
): T[] {
  const kept = rows.filter((r) => !bands.some((b) => r.at > b.x1 && r.at < b.x2))
  const breaks = bands.map((b) => ({ ...gap, at: b.x1 }) as T)
  return [...kept, ...breaks].sort((a, b) => a.at - b.at)
}
