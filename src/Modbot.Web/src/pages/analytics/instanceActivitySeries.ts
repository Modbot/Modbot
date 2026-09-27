import type { InstanceActivitySeries } from '@/lib/api'
import { breakAtBands, timeBands } from '../../components/charts/coverage.ts'

/**
 * The instance activity chart's plain functions. No components, so a test can run them under node
 * and the chart keeps its fast-refresh boundary.
 */

/** One chart row: the moment as a number, because the axis is a number line. */
export type ActivityRow = { at: number; people: number; instances: number }

/**
 * The series as rows, with the last value carried to the end of the range.
 *
 * A staircase whose last step is the last reading stops drawing wherever the count last moved,
 * which for a quiet evening is hours before the right-hand edge. The closing row repeats the last
 * value at the range's end, which is what the data says: nothing has changed since.
 */
export function toActivityRows(series: InstanceActivitySeries): ActivityRow[] {
  const rows = series.points
    .map((p) => ({ at: Date.parse(p.at), people: p.people, instances: p.instances }))
    .filter((r) => Number.isFinite(r.at))
    .sort((a, b) => a.at - b.at)

  const end = Date.parse(series.to)
  const last = rows[rows.length - 1]

  if (last && Number.isFinite(end) && end > last.at) rows.push({ ...last, at: end })

  return rows
}

/** A chart row that may be a break in the line: nulls where nothing was counted. */
export type ActivityChartRow = { at: number; people: number | null; instances: number | null }

/**
 * The rows the chart draws and its missing-day bands. The staircase breaks across every day an
 * instance was open and never counted, rather than holding the last count straight across it, and
 * a striped band sits behind those days.
 */
export function activityChartRows(
  series: InstanceActivitySeries,
): { rows: ActivityChartRow[]; bands: { x1: number; x2: number }[] } {
  const bands = timeBands(series.daysWithoutHeadCounts ?? [], Date.parse(series.from), Date.parse(series.to))
  const rows: ActivityChartRow[] = toActivityRows(series)
  return { rows: breakAtBands(rows, bands, { people: null, instances: null }), bands }
}

/** A stretch of time with at least one instance open. `twoOrMore` when two or more were open throughout. */
export type OpenStretch = { x1: number; x2: number; twoOrMore: boolean }

/**
 * The stretches the chart shades behind the people line: one per run of time with an instance
 * open, split where the count crosses between one and two or more.
 *
 * Read as the staircase is: each row's count holds from its moment until the next row's. The last
 * row holds for no time, since it is the range's end (or the last reading, which the line also
 * stops at). A break in the line is no instance and no shading, so a day nobody counted shows its
 * stripes and never looks open.
 */
export function openStretches(rows: readonly ActivityChartRow[]): OpenStretch[] {
  const out: OpenStretch[] = []

  for (let i = 0; i + 1 < rows.length; i++) {
    const x1 = rows[i].at
    const x2 = rows[i + 1].at
    const count = rows[i].instances
    if (!(x2 > x1) || count === null || count < 1) continue

    const twoOrMore = count >= 2
    const last = out[out.length - 1]
    if (last && last.x2 === x1 && last.twoOrMore === twoOrMore) last.x2 = x2
    else out.push({ x1, x2, twoOrMore })
  }

  return out
}

/** The step lengths a whole-number scale may use, smallest first. Past the list it goes on by tens. */
const WHOLE_STEPS = [1, 2, 5, 10, 20, 25, 50]

/**
 * Whole-number ticks from 0 to just past `max`, at most `most` of them, and the scale ends on the
 * last one.
 *
 * Left to the chart library, a scale that allows no fractions still asks for five ticks, so a line
 * that peaks at one instance was drawn on 0 to 4 and reached a quarter of the way up. And the
 * people scale, allowed fractions, put half a person at 0.5. Here the step is the smallest one that
 * fits, so a peak of 1 is drawn on 0 to 1, and a peak of 6 on 0, 2, 4, 6.
 */
export function wholeTicks(max: number, most = 5): number[] {
  const top = Number.isFinite(max) && max > 0 ? Math.ceil(max) : 1
  const limit = Math.max(2, most)

  let step = 0
  for (let scale = 1; step === 0; scale *= 100) {
    step = WHOLE_STEPS.map((s) => s * scale).find((s) => Math.ceil(top / s) + 1 <= limit) ?? 0
  }

  const ticks: number[] = []
  for (let t = 0; t < top + step; t += step) ticks.push(t)
  return ticks
}

/** The highest value of one key across the rows, ignoring breaks in the line. Nought with none. */export function highest(rows: readonly ActivityChartRow[], key: 'people' | 'instances'): number {
  return rows.reduce((m, r) => {
    const value = r[key]
    return value === null ? m : Math.max(m, value)
  }, 0)
}
