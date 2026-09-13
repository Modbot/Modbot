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
import { bytes } from './units'

/**
 * The storage estimate, drawn.
 *
 * The one measured value, today's size, is the solid dot the line starts from; everything to
 * the right of it is arithmetic. How much to trust that arithmetic is shown three ways, because
 * it is the whole point of the chart:
 *
 * - the stroke: solid on a month or more of data, dashed under a month, dotted under a day.
 *   Dashed carries exactly one meaning on this page — "not measured";
 * - a shaded wedge for the two lower grades, spanning the same line at a slower and a faster
 *   rate — the widths are fixed per grade and named in the caption, not computed intervals;
 * - the caption, which says in words how many days the estimate rests on.
 *
 * The server does the arithmetic. Every number here is read off the horizons it sent, so the
 * chart and the table beneath it cannot disagree.
 */

type Storage = DataSettings['storage']

/** How far the shaded wedge reaches either side of the line, as multiples of the measured rate. */
const SPREAD: Record<Storage['confidence'], [number, number] | null> = {
  Good: null,
  Low: [0.75, 1.5],
  Insufficient: [0.5, 2],
}

const HEIGHT = 224
const ESTIMATE = seriesColor(1)

const UNITS = [
  { label: 'TB', size: 1024 ** 4 },
  { label: 'GB', size: 1024 ** 3 },
  { label: 'MB', size: 1024 ** 2 },
  { label: 'KB', size: 1024 },
  { label: 'B', size: 1 },
]

/**
 * Axis ticks in whichever unit keeps the labels short.
 *
 * Computed in the display unit rather than in bytes, because 1, 2, 5 steps in bytes land on
 * values like 500,000,000 — which is 477 MB, and reads as a mistake.
 */
function byteTicks(max: number): { values: number[]; hi: number; label: (v: number) => string } {
  const unit = UNITS.find((u) => max >= u.size) ?? UNITS[UNITS.length - 1]
  const top = Math.max(max / unit.size, 1e-9)
  const rough = top / 4
  const magnitude = 10 ** Math.floor(Math.log10(rough))
  const step =
    [1, 2, 2.5, 5, 10].map((m) => m * magnitude).find((s) => s >= rough) ?? magnitude * 10
  const hi = Math.ceil(top / step) * step

  const values: number[] = []
  for (let v = 0; v <= hi + step / 2 && values.length < 12; v += step) values.push(v * unit.size)

  return {
    values,
    hi: hi * unit.size,
    label: (v) => {
      const n = v / unit.size
      return `${Number.isInteger(n) ? n : n.toFixed(1)} ${unit.label}`
    },
  }
}

function monthsLabel(months: number): string {
  if (months === 0) return 'Today'
  if (months % 12 === 0) return months === 12 ? '1 year' : `${months / 12} years`
  return `${months} months`
}

function inMonths(months: number): string {
  if (months < 0.5) return 'Today'
  const m = Math.round(months)
  if (m % 12 === 0) return m === 12 ? 'In 1 year' : `In ${m / 12} years`
  return m === 1 ? 'In 1 month' : `In ${m} months`
}

function daysLabel(days: number): string {
  if (days < 1) return 'less than a day'
  const d = Math.round(days)
  return d === 1 ? '1 day' : `${d} days`
}

type Point = { months: number; estimate: number; range?: [number, number] }

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
}: {
  storage: Storage
  /** The disk size the operator typed, in bytes, or null when the field is empty. */
  capacityBytes: number | null
}) {
  const tokens = useChartTokens()

  const horizons = [...storage.horizons].sort((a, b) => a.months - b.months)
  const last = horizons[horizons.length - 1]
  const anchor = storage.bytes
  const maxMonths = last?.months ?? 12
  const perMonth = last ? (last.estimatedBytes - anchor) / last.months : 0
  const growing = perMonth > 0
  const spread = growing ? SPREAD[storage.confidence] : null
  const costPerByte =
    last && last.monthlyCost !== null && last.estimatedBytes > 0
      ? last.monthlyCost / last.estimatedBytes
      : null

  const at = (months: number, rate = 1) => anchor + perMonth * rate * months
  const topEstimate = at(maxMonths, spread ? spread[1] : 1)

  // The disk line joins the plot only if it is within reach; a 500 GB line above a 2 GB history
  // would flatten the history into the axis and say nothing the room-left figure does not.
  const capacityInView =
    capacityBytes !== null && capacityBytes <= Math.max(topEstimate, anchor) * 3
  const scale = byteTicks(Math.max(topEstimate, anchor, capacityInView ? capacityBytes : 0, 1))

  // One point per month, so the hover snaps to whole months and reads "In 9 months".
  const points: Point[] = Array.from({ length: maxMonths + 1 }, (_, m) => ({
    months: m,
    estimate: at(m),
    ...(spread ? { range: [at(m, spread[0]), at(m, spread[1])] as [number, number] } : {}),
  }))

  const crossing =
    capacityBytes !== null && growing && capacityBytes > anchor
      ? (capacityBytes - anchor) / perMonth
      : null

  const dash =
    storage.confidence === 'Good' ? undefined : storage.confidence === 'Low' ? '6 4' : '2 4'

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
  const diskLabelRight = crossing !== null && crossing < maxMonths / 2

  return (
    <div className="flex flex-col gap-2">
      <Legend spread={spread !== null} disk={capacityInView} />

      <ComposedChart
        responsive
        data={points}
        // Right margin fits half of "2 years" at VR type size, where the last tick label is
        // centred on the plot edge and would otherwise be cut off.
        margin={{ top: 18, right: 28, bottom: 0, left: 0 }}
        style={{ width: '100%', height: HEIGHT }}
        role="img"
        aria-label={`Estimated database size over the next ${monthsLabel(maxMonths).toLowerCase()}`}
      >
        <CartesianGrid vertical={false} stroke={GRID_COLOR} strokeWidth={tokens.hairline} />
        <XAxis
          {...AXIS}
          dataKey="months"
          type="number"
          domain={[0, maxMonths]}
          ticks={[0, ...horizons.map((h) => h.months)]}
          tickFormatter={monthsLabel}
          tick={axisTick(tokens)}
        />
        <YAxis
          {...AXIS}
          domain={[0, scale.hi]}
          ticks={scale.values}
          tickFormatter={scale.label}
          tick={axisTick(tokens)}
          width="auto"
        />

        {/* The wedge: the same straight line at a slower and a faster rate. */}
        {spread && (
          <Area
            dataKey="range"
            stroke="none"
            fill={ESTIMATE}
            fillOpacity={0.14}
            activeDot={false}
            isAnimationActive={false}
          />
        )}

        {/* The wash runs to zero: the distance from zero is the message. */}
        <Area
          dataKey="estimate"
          stroke="none"
          fill={ESTIMATE}
          fillOpacity={0.08}
          activeDot={false}
          isAnimationActive={false}
        />
        <Line
          dataKey="estimate"
          stroke={ESTIMATE}
          strokeWidth={tokens.stroke}
          strokeDasharray={dash}
          strokeLinecap="round"
          dot={false}
          activeDot={{ r: 4, fill: ESTIMATE, stroke: SURFACE, strokeWidth: 2 }}
          isAnimationActive={false}
        />

        {capacityInView && (
          <ReferenceLine
            y={capacityBytes}
            stroke={MUTED_TEXT}
            strokeWidth={tokens.hairline}
            // A line hugging the axis has no room for a label above it that clears the tick
            // labels; the legend names the line and the caption carries the size.
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
        {crossing !== null && crossing <= maxMonths && capacityBytes !== null && (
          <ReferenceDot
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
                : crossing > maxMonths * 0.85
                  ? 'below-left'
                  : 'below-right',
              'var(--destructive)',
              tokens.fontSize,
            )}
          />
        )}

        {/* Today: the one measured point, with a surface ring so it reads over the line. */}
        <ReferenceDot
          x={0}
          y={anchor}
          r={4.5}
          fill={ESTIMATE}
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
            x={maxMonths}
            y={at(maxMonths)}
            r={3}
            fill={ESTIMATE}
            stroke="none"
            label={dotLabel(bytes(at(maxMonths)), 'above-left', TEXT, tokens.fontSize)}
          />
        )}

        <Tooltip
          cursor={{ stroke: GRID_COLOR, strokeWidth: tokens.hairline }}
          isAnimationActive={false}
          content={({ active, payload }) => {
            const point = payload?.[0]?.payload as Point | undefined
            if (!active || !point) return null
            return (
              <ChartTooltipFrame title={inMonths(point.months)}>
                <ChartTooltipRow
                  value={`${point.months === 0 ? '' : '~'}${bytes(point.estimate)}`}
                  label={
                    costPerByte !== null
                      ? `· $${(point.estimate * costPerByte).toFixed(2)}/month`
                      : undefined
                  }
                />
                {point.range && point.months > 0 && (
                  <div className="text-muted-foreground tabular-nums">
                    could be {bytes(point.range[0])} – {bytes(point.range[1])}
                  </div>
                )}
              </ChartTooltipFrame>
            )
          }}
        />
      </ComposedChart>

      <Caption
        storage={storage}
        growing={growing}
        capacityBytes={capacityBytes}
        capacityInView={capacityInView}
      />
    </div>
  )
}

function Legend({ spread, disk }: { spread: boolean; disk: boolean }) {
  return (
    <div
      className="flex flex-wrap items-center gap-x-4 gap-y-1 text-muted-foreground"
      style={{ fontSize: 'var(--text-small)' }}
    >
      <span className="flex items-center gap-1.5">
        <span className="size-2.5 shrink-0 rounded-full" style={{ background: ESTIMATE }} />
        Estimate
      </span>
      {spread && (
        <span className="flex items-center gap-1.5">
          <span
            className="h-2.5 w-4 shrink-0 rounded-sm"
            style={{ background: ESTIMATE, opacity: 0.25 }}
          />
          Slower or faster than measured
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

/**
 * What the line rests on, in words. This is the part that is not allowed to be subtle: an
 * estimate from two hours of history is still shown, and the sentence beside it is what keeps
 * the number from being believed more than it deserves.
 */
function Caption({
  storage,
  growing,
  capacityBytes,
  capacityInView,
}: {
  storage: Storage
  growing: boolean
  capacityBytes: number | null
  capacityInView: boolean
}) {
  const days = daysLabel(storage.observedDays)

  let basis: string
  if (!growing) {
    basis =
      storage.observedDays < 1
        ? 'Nothing has arrived yet, so the line is flat. The estimate starts moving with the first day of facts.'
        : `Nothing arrived in the last ${days}, so the line is flat.`
  } else if (storage.confidence === 'Good') {
    basis = `Estimate is based on ${days} of data. It is a straight line, which real growth is not — a group that opens more instances generates more facts per member — so treat it as an order of magnitude.`
  } else if (storage.confidence === 'Low') {
    basis = `Estimate is based on ${days} of data — treat it as rough. The shaded area is the same line at three-quarters and one-and-a-half times the measured rate; a single busy weekend still moves it that much.`
  } else {
    basis = `Estimate is based on ${days} of data — a guess, and one that will change a lot by tomorrow. The shaded area is the same line at half and double the measured rate.`
  }

  const over = capacityBytes !== null && storage.bytes >= capacityBytes

  return (
    <div
      className="flex flex-col gap-1 text-muted-foreground"
      style={{ fontSize: 'var(--text-small)' }}
    >
      <p>{basis}</p>
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
                Room left: {bytes(capacityBytes - storage.bytes)}
              </span>{' '}
              of {bytes(capacityBytes)}.
              {!capacityInView && ' The disk is well above the top of this chart.'}
              {storage.capacityExhausted && (
                <span className="text-destructive">
                  {' '}
                  At this rate it fills around{' '}
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
