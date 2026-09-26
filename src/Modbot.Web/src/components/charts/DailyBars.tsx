import { Bar, BarChart, CartesianGrid, Rectangle, Tooltip, XAxis, YAxis, type BarShapeProps } from 'recharts'
import { ChartFrame } from './ChartFrame'
import { barRows, type BarRow, type DayMarks } from './coverage'
import { MissingBands, Stripes } from './marks'
import { rechartsTooltip } from './rechartsTooltip'
import { compactNumber, longDay, shortDay, tickDays, type DayPoint } from './format'
import { chartHeight, seriesColor, type SeriesSlot } from './theme'
import { useStripeId } from './useStripeId'

/** `one` is the label for a value of exactly one, when `label` is a plural noun: "action" for "actions". */
export type DaySeries = { key: string; label: string; one?: string; points: DayPoint[]; slot: SeriesSlot }

/** How tall the mark for a day that was really nought is. Thin, and on the baseline. */
const ZERO_MARK = 2

/**
 * Daily counts as columns, one column per day, several series side by side or stacked.
 *
 * Columns rather than a line because these are counts of discrete events, and a line between two
 * days implies values in between that were never measured.
 *
 * Three kinds of day are drawn differently, so none passes for another (`coverage.ts`):
 * - a day the source has nothing for (`missing`) has no column and a striped band behind it;
 * - a day Modbot was watching and nothing happened has a thin mark on the baseline, because no
 *   bans is zero bans, and a real zero should not look like a gap;
 * - the day that is not over yet (`today`) is a dashed outline rather than a solid column.
 */
export function DailyBars({
  from,
  to,
  series,
  stacked = false,
  height = chartHeight.regular,
  emptyText,
  legend,
  format,
  missing,
  today,
}: {
  from: string
  to: string
  series: DaySeries[]
  stacked?: boolean
  height?: number
  emptyText?: string
  /** Names and colours for the series, left out with the plot when there is nothing to draw. */
  legend?: { label: string; slot: SeriesSlot }[]
  /** How a value is written on the axis and in the tooltip, for something that is not a count -- money. */
  format?: (value: number) => string
} & DayMarks) {
  const stripeId = useStripeId()
  const { rows, runs } = barRows(from, to, series, { missing, today })
  const days = rows.map((r) => r.day)
  const marks = new Map(rows.map((r) => [r.day, r.mark]))
  const names = Object.fromEntries(series.map((s) => [s.key, s.label]))
  const ones = Object.fromEntries(series.flatMap((s) => (s.one ? [[s.key, s.one]] : [])))
  const empty = series.every((s) => s.points.every((p) => p.value === 0))

  const shape = (s: DaySeries, index: number) => (props: BarShapeProps) => {
    const row = props.payload as BarRow | undefined
    const color = seriesColor(s.slot)

    if (!row || row.mark === 'missing') return <g />

    if (row[s.key] === 0) {
      // One mark per day: stacked columns share a baseline, so only the first series draws it,
      // and only when the whole stack is nought.
      if (stacked && (index > 0 || series.some((other) => row[other.key] !== 0))) return <g />
      return (
        <rect
          x={props.x}
          y={props.y - ZERO_MARK}
          width={props.width}
          height={ZERO_MARK}
          style={{ fill: 'var(--muted-foreground)' }}
        />
      )
    }

    if (row.mark === 'today') {
      return (
        <Rectangle
          x={props.x}
          y={props.y}
          width={props.width}
          height={props.height}
          radius={props.radius}
          fill="none"
          stroke={color}
          strokeDasharray="4 3"
          style={{ strokeWidth: 'calc(var(--hairline) * 1.5)' }}
        />
      )
    }

    return (
      <Rectangle
        x={props.x}
        y={props.y}
        width={props.width}
        height={props.height}
        radius={props.radius}
        fill={color}
      />
    )
  }

  return (
    <ChartFrame height={height} empty={empty} emptyText={emptyText} legend={legend}>
      <BarChart data={rows} margin={{ top: 4, right: 8, bottom: 0, left: 0 }} barCategoryGap="20%">
        <Stripes id={stripeId} />
        <CartesianGrid vertical={false} />
        <XAxis dataKey="day" ticks={tickDays(days)} tickFormatter={shortDay} tickLine={false} axisLine={false} minTickGap={16} />
        <YAxis
          width="auto"
          allowDecimals={format !== undefined}
          tickFormatter={format ?? compactNumber}
          tickLine={false}
          axisLine={false}
        />
        <MissingBands stripeId={stripeId} bands={runs.map((r) => ({ x1: days[r.first], x2: days[r.last] }))} />
        <Tooltip
          content={rechartsTooltip(longDay, names, format, ones, (day) => marks.get(day))}
          cursor={{ fill: 'var(--accent)', fillOpacity: 0.4 }}
        />
        {series.map((s, i) => (
          <Bar
            key={s.key}
            dataKey={s.key}
            name={s.label}
            fill={seriesColor(s.slot)}
            stackId={stacked ? 'stack' : undefined}
            radius={stacked ? 0 : [3, 3, 0, 0]}
            maxBarSize={28}
            isAnimationActive={false}
            shape={shape(s, i)}
          />
        ))}
      </BarChart>
    </ChartFrame>
  )
}
