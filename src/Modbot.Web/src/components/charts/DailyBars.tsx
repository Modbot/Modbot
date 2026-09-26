import { Bar, BarChart, CartesianGrid, Tooltip, XAxis, YAxis } from 'recharts'
import { ChartFrame } from './ChartFrame'
import { rechartsTooltip } from './rechartsTooltip'
import { compactNumber, longDay, mergeDays, shortDay, tickDays, type DayPoint } from './format'
import { chartHeight, seriesColor, type SeriesSlot } from './theme'

/** `one` is the label for a value of exactly one, when `label` is a plural noun: "action" for "actions". */
export type DaySeries = { key: string; label: string; one?: string; points: DayPoint[]; slot: SeriesSlot }

/**
 * Daily counts as columns, one column per day, several series side by side or stacked.
 *
 * Columns rather than a line because these are counts of discrete events, and a line between two
 * days implies values in between that were never measured. Days with nothing recorded are drawn
 * as zero -- no bans is zero bans, not an unknown.
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
}) {
  const rows = mergeDays(from, to, series, 'zero')
  const days = rows.map((r) => String(r.day))
  const names = Object.fromEntries(series.map((s) => [s.key, s.label]))
  const ones = Object.fromEntries(series.flatMap((s) => (s.one ? [[s.key, s.one]] : [])))
  const empty = series.every((s) => s.points.every((p) => p.value === 0))

  return (
    <ChartFrame height={height} empty={empty} emptyText={emptyText} legend={legend}>
      <BarChart data={rows} margin={{ top: 4, right: 8, bottom: 0, left: 0 }} barCategoryGap="20%">
        <CartesianGrid vertical={false} />
        <XAxis dataKey="day" ticks={tickDays(days)} tickFormatter={shortDay} tickLine={false} axisLine={false} minTickGap={16} />
        <YAxis
          width="auto"
          allowDecimals={format !== undefined}
          tickFormatter={format ?? compactNumber}
          tickLine={false}
          axisLine={false}
        />
        <Tooltip content={rechartsTooltip(longDay, names, format, ones)} cursor={{ fill: 'var(--accent)', fillOpacity: 0.4 }} />
        {series.map((s) => (
          <Bar
            key={s.key}
            dataKey={s.key}
            name={s.label}
            fill={seriesColor(s.slot)}
            stackId={stacked ? 'stack' : undefined}
            radius={stacked ? 0 : [3, 3, 0, 0]}
            maxBarSize={28}
            isAnimationActive={false}
          />
        ))}
      </BarChart>
    </ChartFrame>
  )
}
