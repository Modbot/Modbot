import { CartesianGrid, Line, LineChart, Tooltip, XAxis, YAxis } from 'recharts'
import { ChartFrame } from './ChartFrame'
import { rechartsTooltip } from './rechartsTooltip'
import { compactNumber, longDay, mergeDays, shortDay, tickDays } from './format'
import type { DaySeries } from './DailyBars'
import { chartHeight, seriesColor } from './theme'

/**
 * One or more values over time, as lines.
 *
 * A line because the question is a shape -- is this going up. `mode` says how a day with no row
 * is filled: `carry` for a level (a headcount stays what it was), `zero` for a count.
 *
 * `zeroBased` says whether the axis must include zero. True for anything that is a quantity of
 * events, where the distance from zero is the message. False for a level -- a membership of
 * 14,208 that moved by 400 over three months is a flat line against a zero axis, and the movement
 * is the entire reason anyone opened the chart.
 */
export function DailyLine({
  from,
  to,
  series,
  mode = 'zero',
  zeroBased = true,
  height = chartHeight.regular,
  emptyText,
  format,
}: {
  from: string
  to: string
  series: DaySeries[]
  mode?: 'zero' | 'carry'
  zeroBased?: boolean
  height?: number
  emptyText?: string
  format?: (value: number) => string
}) {
  const rows = mergeDays(from, to, series, mode)
  const days = rows.map((r) => String(r.day))
  const names = Object.fromEntries(series.map((s) => [s.key, s.label]))
  const empty = series.every((s) => s.points.length === 0)

  return (
    <ChartFrame height={height} empty={empty} emptyText={emptyText}>
      <LineChart data={rows} margin={{ top: 6, right: 8, bottom: 0, left: 0 }}>
        <CartesianGrid vertical={false} />
        <XAxis dataKey="day" ticks={tickDays(days)} tickFormatter={shortDay} tickLine={false} axisLine={false} minTickGap={16} />
        <YAxis
          width={44}
          domain={zeroBased ? [0, 'auto'] : ['auto', 'auto']}
          tickFormatter={format ?? compactNumber}
          tickLine={false}
          axisLine={false}
        />
        <Tooltip content={rechartsTooltip(longDay, names, format)} cursor={{ stroke: 'var(--chart-grid)' }} />
        {series.map((s) => (
          <Line
            key={s.key}
            type="monotone"
            dataKey={s.key}
            name={s.label}
            stroke={seriesColor(s.slot)}
            strokeWidth={2}
            dot={false}
            activeDot={{ r: 4, strokeWidth: 2, stroke: 'var(--card)' }}
            connectNulls
            isAnimationActive={false}
          />
        ))}
      </LineChart>
    </ChartFrame>
  )
}
