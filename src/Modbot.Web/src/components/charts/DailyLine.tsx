import { CartesianGrid, Line, LineChart, Tooltip, XAxis, YAxis, type DotItemDotProps } from 'recharts'
import { ChartFrame } from './ChartFrame'
import { carriedKey, lineRows, type DayMarks } from './coverage'
import { HollowDot, MissingBands, Stripes } from './marks'
import { rechartsTooltip } from './rechartsTooltip'
import { compactNumber, longDay, shortDay, tickDays } from './format'
import type { DaySeries } from './DailyBars'
import { chartHeight, seriesColor, type SeriesSlot } from './theme'
import { useStripeId } from './useStripeId'

/**
 * One or more values over time, as lines.
 *
 * A line because the question is a shape -- is this going up. `mode` says how a day with no row
 * is filled: `carry` for a level (a headcount stays what it was), `zero` for a count. A carried
 * stretch is drawn dashed, because nothing was measured on those days; the value is the last one
 * that was.
 *
 * `zeroBased` says whether the axis must include zero. True for anything that is a quantity of
 * events, where the distance from zero is the message. False for a level -- a membership of
 * 14,208 that moved by 400 over three months is a flat line against a zero axis, and the movement
 * is the entire reason anyone opened the chart.
 *
 * The axis is the day's position, a number, rather than the day's name, so a striped band can
 * cover exactly the days it means: a missing day breaks the line and sits in a band (`missing`),
 * and the day not over yet is a hollow point (`today`).
 */
export function DailyLine({
  from,
  to,
  series,
  mode = 'zero',
  zeroBased = true,
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
  mode?: 'zero' | 'carry'
  zeroBased?: boolean
  height?: number
  emptyText?: string
  /** Names and colours for the series, left out with the plot when there is nothing to draw. */
  legend?: { label: string; slot: SeriesSlot }[]
  format?: (value: number) => string
} & DayMarks) {
  const stripeId = useStripeId()
  const { rows, runs } = lineRows(from, to, series, mode, { missing, today })
  const days = rows.map((r) => r.day)
  const ticks = tickDays(days).map((d) => days.indexOf(d))
  const dayAt = (i: string | number) => days[Number(i)] ?? ''
  const names = Object.fromEntries(series.flatMap((s) => [[s.key, s.label], [carriedKey(s.key), s.label]]))
  const ones = Object.fromEntries(
    series.flatMap((s) => (s.one ? [[s.key, s.one], [carriedKey(s.key), s.one]] : [])),
  )
  const empty = series.every((s) => s.points.length === 0)

  const todayDot = (color: string) => ({ cx, cy, index }: DotItemDotProps) =>
    rows[index]?.mark === 'today' ? <HollowDot key={`today-${index}`} cx={cx} cy={cy} color={color} /> : null

  return (
    <ChartFrame height={height} empty={empty} emptyText={emptyText} legend={legend}>
      <LineChart data={rows} margin={{ top: 6, right: 8, bottom: 0, left: 0 }}>
        <Stripes id={stripeId} />
        <CartesianGrid vertical={false} />
        <XAxis
          dataKey="i"
          type="number"
          domain={[-0.5, Math.max(days.length - 0.5, 0.5)]}
          ticks={ticks}
          tickFormatter={(i: number) => shortDay(dayAt(i))}
          allowDecimals={false}
          tickLine={false}
          axisLine={false}
          minTickGap={16}
        />
        <YAxis
          width="auto"
          domain={zeroBased ? [0, 'auto'] : ['auto', 'auto']}
          tickFormatter={format ?? compactNumber}
          tickLine={false}
          axisLine={false}
        />
        <MissingBands stripeId={stripeId} bands={runs.map((r) => ({ x1: r.first - 0.5, x2: r.last + 0.5 }))} />
        <Tooltip
          content={rechartsTooltip((i) => longDay(dayAt(i)), names, format, ones, (i) => rows[Number(i)]?.mark)}
          cursor={{ stroke: 'var(--chart-grid)' }}
        />
        {series.map((s) => (
          <Line
            key={s.key}
            type="monotone"
            dataKey={s.key}
            name={s.label}
            stroke={seriesColor(s.slot)}
            strokeWidth={2}
            dot={todayDot(seriesColor(s.slot))}
            activeDot={{ r: 4, strokeWidth: 2, stroke: 'var(--card)' }}
            connectNulls={false}
            isAnimationActive={false}
          />
        ))}
        {mode === 'carry' &&
          series.map((s) => (
            <Line
              key={carriedKey(s.key)}
              type="monotone"
              dataKey={carriedKey(s.key)}
              name={s.label}
              stroke={seriesColor(s.slot)}
              strokeWidth={2}
              strokeDasharray="5 4"
              dot={false}
              activeDot={false}
              connectNulls={false}
              legendType="none"
              isAnimationActive={false}
            />
          ))}
      </LineChart>
    </ChartFrame>
  )
}
