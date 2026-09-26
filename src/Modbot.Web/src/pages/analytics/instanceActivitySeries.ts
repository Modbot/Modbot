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
