import {
  Area,
  CartesianGrid,
  ComposedChart,
  Line,
  ReferenceDot,
  ReferenceLine,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import { ChartTooltipFrame, ChartTooltipRow } from './StorageTooltip'
import {
  AXIS,
  GRID_COLOR,
  MUTED_TEXT,
  SURFACE,
  TEXT,
  axisTick,
  plotLabel,
  seriesColor,
  useChartTokens,
} from './storageChartTheme'
import type { DataSettings } from '@/lib/api'
import { DAY_MS, GB, bytes, gigabytes, hasPlentyOfStorage } from './units'

/**
 * Storage over time: the past year as recorded, today as measured, and the year ahead as an
 * estimate.
 *
 * Today sits in the middle once there is a year of recorded days. Before that, the left side
 * narrows to the days that exist, so no width is spent on time before recording began and today
 * moves left in proportion.
 *
 * Recorded days are solid and the estimate is dashed, which is the one meaning dashes carry on
 * this page. Under a month of data the estimate also gets a shaded wedge: the same line at a
 * slower and a faster rate, with fixed widths per grade rather than computed intervals.
 *
 * When a per-GB cost is typed, the estimate is priced and drawn as a second line on its own axis
 * on the right.
 */

type Storage = DataSettings['storage']

/** How far the shaded wedge reaches either side of the line, as multiples of the measured rate. */
const SPREAD: Record<Storage['confidence'], [number, number] | null> = {
  Good: null,
  Low: [0.75, 1.5],
  Insufficient: [0.5, 2],
}

const HEIGHT = 224
const SIZE = seriesColor(1)
const COST = seriesColor(2)

/** Mean Gregorian month, so estimate points land a whole month apart. */
const DAYS_PER_MONTH = 30.436875

/** Each side of today reaches a year: twelve of those months. */
const YEAR_DAYS = 12 * DAYS_PER_MONTH

const UNITS = [
  { label: 'TB', size: 1024 ** 4 },
  { label: 'GB', size: 1024 ** 3 },
  { label: 'MB', size: 1024 ** 2 },
  { label: 'KB', size: 1024 },
  { label: 'B', size: 1 },
]

/** The smallest 1, 2, 2.5 or 5 step at or above the rough one, at its power of ten. */
function niceStep(rough: number): number {
  const magnitude = 10 ** Math.floor(Math.log10(rough))
  return [1, 2, 2.5, 5, 10].map((m) => m * magnitude).find((s) => s >= rough) ?? magnitude * 10
}

type Scale = { values: number[]; hi: number; label: (v: number) => string }

/**
 * Axis ticks in whichever unit keeps the labels short.
 *
 * Computed in the display unit rather than in bytes, because 1, 2, 5 steps in bytes land on
 * values like 500,000,000 — which is 477 MB, and reads as a mistake.
 */
function byteTicks(max: number): Scale {
  const unit = UNITS.find((u) => max >= u.size) ?? UNITS[UNITS.length - 1]
  const top = Math.max(max / unit.size, 1e-9)
  const step = niceStep(top / 4)
  const hi = Math.ceil(top / step) * step

  const values: number[] = []
  for (let i = 0; i * step <= hi + step / 2 && values.length < 12; i++)
    values.push(i * step * unit.size)

  return {
    values,
    hi: hi * unit.size,
    label: (v) => {
      const n = v / unit.size
      return `${Number.isInteger(n) ? n : n.toFixed(1)} ${unit.label}`
    },
  }
}

/** Dollar ticks for the cost axis, down to tenths of a cent for a small database. */
function costTicks(max: number): Scale {
  const top = Math.max(max, 0.001)
  const step = niceStep(top / 4)
  const hi = Math.ceil(top / step) * step
  const decimals = step < 0.01 ? 3 : Number.isInteger(step) ? 0 : 2

  const values: number[] = []
  for (let i = 0; i * step <= hi + step / 2 && values.length < 12; i++) values.push(i * step)

  return { values, hi, label: (v) => `$${v.toFixed(decimals)}` }
}

function inMonths(days: number): string {
  const m = Math.round(days / DAYS_PER_MONTH)
  if (m % 12 === 0) return m === 12 ? 'In 1 year' : `In ${m / 12} years`
  return m === 1 ? 'In 1 month' : `In ${m} months`
}

function dayIndex(ms: number): number {
  return Math.floor(ms / DAY_MS)
}

type Point = {
  /** Days from today: negative is recorded history, positive is the estimate. */
  x: number
  measured?: number
  estimate?: number
  range?: [number, number]
  cost?: number
}

/**
 * A label beside a reference dot, offset diagonally so it clears the line the dot sits on.
 * Recharts' built-in positions are axis-aligned (top, right, …), and on a rising line every
 * one of them lands on the stroke.
 */
function dotLabel(
  text: string,
  where: 'above-right' | 'above-left' | 'below-right' | 'below-left',
  color: string,
  fontSize: number,
) {
  const right = where.endsWith('right')
  const above = where.startsWith('above')
  // Typed loosely because recharts types a label render function's props as `any`.
  return (props: { viewBox?: { x?: number; y?: number; width?: number; height?: number } }) => {
    const box = props.viewBox ?? {}
    const cx = (box.x ?? 0) + (box.width ?? 0) / 2
    const cy = (box.y ?? 0) + (box.height ?? 0) / 2
    return (
      <text
        x={cx + (right ? 9 : -9)}
        y={cy + (above ? -9 : 15)}
        textAnchor={right ? 'start' : 'end'}
        fill={color}
        fontSize={fontSize}
        fontWeight={500}
      >
        {text}
      </text>
    )
  }
}

export function StorageChart({
  storage,
  capacityBytes,
  costPerGbMonth,
}: {
  storage: Storage
  /** The disk size the operator typed, in bytes, or null when the field is empty. */
  capacityBytes: number | null
  /** The price the operator typed per GB per month, or null when the field is empty. */
  costPerGbMonth: number | null
}) {
  const tokens = useChartTokens()

  const anchor = storage.bytes
  const perDay = storage.bytesPerDay
  const growing = perDay > 0
  const spread = growing ? SPREAD[storage.confidence] : null

  const measuredMs = Date.parse(storage.measuredAt)
  const today = dayIndex(measuredMs)

  // Today's own row, if it has been recorded yet, is replaced by the live measurement at x = 0.
  const past: Point[] = storage.history
    .map((d) => ({ x: dayIndex(Date.parse(`${d.day}T00:00:00Z`)) - today, measured: d.bytes }))
    .filter((p) => p.x < 0 && p.x >= -YEAR_DAYS)
  const pastDays = past.length > 0 ? -Math.min(...past.map((p) => p.x)) : 0

  const at = (days: number, rate = 1) => anchor + perDay * rate * days
  const costOf = (size: number) =>
    costPerGbMonth === null ? undefined : (size / GB) * costPerGbMonth

  // One estimate point per month, so the hover snaps to whole months and reads "In 9 months".
  const future: Point[] = Array.from({ length: 13 }, (_, m) => {
    const x = m * DAYS_PER_MONTH
    return {
      x,
      estimate: at(x),
      cost: costOf(at(x)),
      ...(spread ? { range: [at(x, spread[0]), at(x, spread[1])] as [number, number] } : {}),
    }
  })
  future[0].measured = anchor

  const points = [...past, ...future]

  const topEstimate = at(YEAR_DAYS, spread ? spread[1] : 1)
  const topPast = Math.max(0, ...past.map((p) => p.measured ?? 0))
  const topData = Math.max(topEstimate, anchor, topPast)

  // The disk line joins the plot only if it is within reach; a 500 GB line above a 2 GB history
  // would flatten the history into the axis and say nothing the storage-left figure does not.
  const capacityInView = capacityBytes !== null && capacityBytes <= topData * 3
  const scale = byteTicks(Math.max(topData, capacityInView ? capacityBytes : 0, 1))
  const costScale = costPerGbMonth === null ? null : costTicks(costOf(at(YEAR_DAYS)) ?? 0)

  const crossing =
    capacityBytes !== null && growing && capacityBytes > anchor
      ? (capacityBytes - anchor) / perDay
      : null

  const dash = storage.confidence === 'Insufficient' ? '2 4' : '6 4'

  // Label placement, in approximate plot pixels. Three labels can end up in one corner when a
  // small disk sits just above a small database: the disk line, "Full", and "today". The rules
  // below keep them apart rather than letting whichever drew last win.
  const PLOT_PX = HEIGHT - 48 // minus top margin and the x-axis strip
  const heightOf = (v: number) => (v / scale.hi) * PLOT_PX
  const diskNearFloor = capacityInView && capacityBytes !== null && heightOf(capacityBytes) < 26
  const diskNearToday =
    capacityInView && capacityBytes !== null && Math.abs(heightOf(capacityBytes - anchor)) < 22
  // The disk label sits at whichever end the estimate's own end label is not: the end label is
  // at top right, so a disk the line crosses early (and then leaves far below) is labelled on
  // the right, and one the line only reaches late is labelled on the left.
  const diskLabelRight = crossing !== null && crossing < YEAR_DAYS / 2

  // A tick every three months, both sides of today, dated rather than counted.
  const ticks: number[] = []
  for (let k = -12; k <= 12; k += 3) {
    const x = k * DAYS_PER_MONTH
    if (x >= -pastDays - 0.5) ticks.push(x)
  }
  const dateOf = (x: number, options: Intl.DateTimeFormatOptions) =>
    new Date(measuredMs + x * DAY_MS).toLocaleDateString(undefined, options)
  const tickLabel = (x: number) =>
    x === 0 ? 'Today' : dateOf(x, { month: 'short', year: 'numeric' })

  return (
    <div className="flex flex-col gap-2">
      <Legend
        measured={past.length > 0}
        spread={spread !== null}
        disk={capacityInView}
        cost={costScale !== null}
      />

      <ComposedChart
        responsive
        data={points}
        // Without a cost axis, the right margin fits half of the last date label, which is
        // centred on the plot edge and would otherwise be cut off.
        margin={{ top: 18, right: costScale ? 8 : 28, bottom: 0, left: 0 }}
        style={{ width: '100%', height: HEIGHT }}
        role="img"
        aria-label={
          past.length > 0
            ? `Database size over the past ${pastDays} days, and estimated over the next year`
            : 'Estimated database size over the next year'
        }
      >
        <CartesianGrid
          yAxisId="size"
          vertical={false}
          stroke={GRID_COLOR}
          strokeWidth={tokens.hairline}
        />
        <XAxis
          {...AXIS}
          dataKey="x"
          type="number"
          domain={[-pastDays, YEAR_DAYS]}
          ticks={ticks}
          interval="preserveStartEnd"
          minTickGap={16}
          tickFormatter={tickLabel}
          tick={axisTick(tokens)}
        />
        <YAxis
          {...AXIS}
          yAxisId="size"
          domain={[0, scale.hi]}
          ticks={scale.values}
          tickFormatter={scale.label}
          tick={axisTick(tokens)}
          width="auto"
        />
        {costScale && (
          <YAxis
            {...AXIS}
            yAxisId="cost"
            orientation="right"
            domain={[0, costScale.hi]}
            ticks={costScale.values}
            tickFormatter={costScale.label}
            tick={axisTick(tokens)}
            width="auto"
          />
        )}

        {/* The wedge: the same straight line at a slower and a faster rate. */}
        {spread && (
          <Area
            yAxisId="size"
            dataKey="range"
            stroke="none"
            fill={SIZE}
            fillOpacity={0.14}
            activeDot={false}
            isAnimationActive={false}
          />
        )}

        {/* The wash runs to zero: the distance from zero is the message. */}
        <Area
          yAxisId="size"
          dataKey="measured"
          stroke="none"
          fill={SIZE}
          fillOpacity={0.08}
          activeDot={false}
          isAnimationActive={false}
        />
        <Area
          yAxisId="size"
          dataKey="estimate"
          stroke="none"
          fill={SIZE}
          fillOpacity={0.08}
          activeDot={false}
          isAnimationActive={false}
        />
        <Line
          yAxisId="size"
          dataKey="measured"
          stroke={SIZE}
          strokeWidth={tokens.stroke}
          strokeLinecap="round"
          dot={false}
          activeDot={{ r: 4, fill: SIZE, stroke: SURFACE, strokeWidth: 2 }}
          isAnimationActive={false}
        />
        <Line
          yAxisId="size"
          dataKey="estimate"
          stroke={SIZE}
          strokeWidth={tokens.stroke}
          strokeDasharray={dash}
          strokeLinecap="round"
          dot={false}
          activeDot={{ r: 4, fill: SIZE, stroke: SURFACE, strokeWidth: 2 }}
          isAnimationActive={false}
        />
        {costScale && (
          <Line
            yAxisId="cost"
            dataKey="cost"
            stroke={COST}
            strokeWidth={tokens.stroke}
            strokeDasharray={dash}
            strokeLinecap="round"
            dot={false}
            activeDot={{ r: 4, fill: COST, stroke: SURFACE, strokeWidth: 2 }}
            isAnimationActive={false}
          />
        )}

        {/* Where recorded history ends and the estimate begins. */}
        {past.length > 0 && (
          <ReferenceLine
            yAxisId="size"
            x={0}
            stroke={GRID_COLOR}
            strokeWidth={tokens.hairline}
          />
        )}

        {capacityInView && (
          <ReferenceLine
            yAxisId="size"
            y={capacityBytes}
            stroke={MUTED_TEXT}
            strokeWidth={tokens.hairline}
            // A line hugging the axis has no room for a label above it that clears the tick
            // labels; the legend names the line.
            label={
              diskNearFloor
                ? undefined
                : {
                    value: `Disk · ${bytes(capacityBytes)}`,
                    position: diskLabelRight ? 'insideTopRight' : 'insideTopLeft',
                    ...plotLabel(tokens),
                  }
            }
          />
        )}

        {/* Where the line meets the disk, if it does inside the window. */}
        {crossing !== null && crossing <= YEAR_DAYS && capacityBytes !== null && (
          <ReferenceDot
            yAxisId="size"
            x={crossing}
            y={capacityBytes}
            r={4.5}
            fill="var(--destructive)"
            stroke={SURFACE}
            strokeWidth={2}
            label={dotLabel(
              'Full',
              diskNearFloor
                ? 'above-right'
                : crossing > YEAR_DAYS * 0.85
                  ? 'below-left'
                  : 'below-right',
              'var(--destructive)',
              tokens.fontSize,
            )}
          />
        )}

        {/* Today: the one live measurement, with a surface ring so it reads over the line. */}
        <ReferenceDot
          yAxisId="size"
          x={0}
          y={anchor}
          r={4.5}
          fill={SIZE}
          stroke={SURFACE}
          strokeWidth={2}
          // Dropped when the disk line runs through the same spot; the size is in the facts
          // beside the chart anyway.
          label={
            diskNearToday
              ? undefined
              : dotLabel(`${bytes(anchor)} today`, 'above-right', TEXT, tokens.fontSize)
          }
        />

        {/* The end value, labelled directly rather than every point. */}
        {growing && (
          <ReferenceDot
            yAxisId="size"
            x={YEAR_DAYS}
            y={at(YEAR_DAYS)}
            r={3}
            fill={SIZE}
            stroke="none"
            label={dotLabel(bytes(at(YEAR_DAYS)), 'above-left', TEXT, tokens.fontSize)}
          />
        )}

        <Tooltip
          cursor={{ stroke: GRID_COLOR, strokeWidth: tokens.hairline }}
          isAnimationActive={false}
          content={({ active, payload }) => {
            const point = payload?.[0]?.payload as Point | undefined
            if (!active || !point) return null
            const size = point.measured ?? point.estimate
            if (size === undefined) return null
            return (
              <ChartTooltipFrame
                title={
                  point.x === 0
                    ? 'Today'
                    : point.x < 0
                      ? dateOf(point.x, { month: 'short', day: 'numeric', year: 'numeric' })
                      : inMonths(point.x)
                }
              >
                <ChartTooltipRow
                  series={costScale ? SIZE : undefined}
                  value={`${point.measured === undefined ? '~' : ''}${bytes(size)}`}
                />
                {point.cost !== undefined && (
                  <ChartTooltipRow series={COST} value={`$${point.cost.toFixed(2)}/mo`} />
                )}
                {point.range && point.x > 0 && (
                  <div className="text-muted-foreground tabular-nums">
                    {bytes(point.range[0])} – {bytes(point.range[1])}
                  </div>
                )}
              </ChartTooltipFrame>
            )
          }}
        />
      </ComposedChart>

      <Caption storage={storage} capacityBytes={capacityBytes} />
    </div>
  )
}

function Legend({
  measured,
  spread,
  disk,
  cost,
}: {
  measured: boolean
  spread: boolean
  disk: boolean
  cost: boolean
}) {
  return (
    <div
      className="flex flex-wrap items-center gap-x-4 gap-y-1 text-muted-foreground"
      style={{ fontSize: 'var(--text-small)' }}
    >
      {measured && (
        <span className="flex items-center gap-1.5">
          <span className="h-0.5 w-4 shrink-0 rounded-full" style={{ background: SIZE }} />
          Recorded
        </span>
      )}
      <span className="flex items-center gap-1.5">
        <span
          className="w-4 shrink-0 border-t-2 border-dashed"
          style={{ borderColor: SIZE }}
        />
        Estimate
      </span>
      {spread && (
        <span className="flex items-center gap-1.5">
          <span
            className="h-2.5 w-4 shrink-0 rounded-sm"
            style={{ background: SIZE, opacity: 0.25 }}
          />
          Slower or faster than measured
        </span>
      )}
      {cost && (
        <span className="flex items-center gap-1.5">
          <span
            className="w-4 shrink-0 border-t-2 border-dashed"
            style={{ borderColor: COST }}
          />
          Cost per month
        </span>
      )}
      {disk && (
        <span className="flex items-center gap-1.5">
          <span className="h-px w-4 shrink-0" style={{ background: MUTED_TEXT }} />
          Disk size
        </span>
      )}
    </div>
  )
}

function Caption({ storage, capacityBytes }: { storage: Storage; capacityBytes: number | null }) {
  const days = Math.round(storage.observedDays)
  const over = capacityBytes !== null && storage.bytes >= capacityBytes
  const plenty = hasPlentyOfStorage(storage, capacityBytes)

  return (
    <div
      className="flex flex-col gap-1 text-muted-foreground"
      style={{ fontSize: 'var(--text-small)' }}
    >
      <p>
        This estimate is based on {days} {days === 1 ? 'day' : 'days'} of data.
      </p>
      {capacityBytes !== null && (
        <p>
          {over ? (
            <span className="text-destructive">
              The database is already {bytes(storage.bytes - capacityBytes)} over a{' '}
              {bytes(capacityBytes)} disk.
            </span>
          ) : (
            <>
              <span className="font-medium text-foreground">
                Storage left: {gigabytes(capacityBytes - storage.bytes)} GB
              </span>{' '}
              of {Number(gigabytes(capacityBytes))} GB.
              {storage.capacityExhausted && (
                <span className={plenty ? undefined : 'text-destructive'}>
                  {' '}
                  At the current rate, it fills around{' '}
                  {new Date(storage.capacityExhausted).toLocaleDateString(undefined, {
                    year: 'numeric',
                    month: 'short',
                    day: 'numeric',
                  })}
                  .
                </span>
              )}
            </>
          )}
        </p>
      )}
    </div>
  )
}
