import { useCallback, useEffect, useRef, useState } from 'react'
import { compact, type Point } from '@/lib/format'

/**
 * Inline SVG charts. No library, and not for bundle-size reasons.
 *
 * Every colour, weight and size here is a CSS custom property from index.css, so the charts move
 * with the theme and with all three densities — including VR, where a 2px line is a suggestion
 * and 11px axis text is mush. A charting library would bring its own theming model and its own
 * idea of a "small" font, and reconciling those with the density tokens is more work than drawing
 * three shapes.
 *
 * The rules the shapes follow, because they are easy to undo by accident:
 *
 * - Series colours are a fixed order, never cycled by rank. A filter that removes a series must
 *   not repaint the survivors, or a reader who learned "bans are orange" is misled.
 * - Marks are thin, grid lines are hairline and recessive, and nothing is dashed.
 * - Identity is never colour alone: every multi-series chart has a legend, and values are labelled
 *   in text tokens rather than in the series colour.
 * - One axis, always. Two measures of different scale are two charts.
 */

/** Measures the container so text renders at its token size rather than being scaled by a viewBox. */
function useWidth(): [React.RefObject<HTMLDivElement | null>, number] {
  const ref = useRef<HTMLDivElement | null>(null)
  const [width, setWidth] = useState(0)

  useEffect(() => {
    const element = ref.current
    if (!element) return

    const observer = new ResizeObserver((entries) => {
      setWidth(Math.floor(entries[0].contentRect.width))
    })

    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  return [ref, width]
}

function shortDay(day: string): string {
  const d = new Date(`${day}T00:00:00Z`)
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' })
}

/**
 * Rounded tick values that read as numbers rather than as arbitrary maxima.
 *
 * Spans negatives as well as positives, because the net-change series genuinely goes below zero
 * when more departures are recorded than arrivals — and an axis labelled only above zero would
 * leave the part of the line that says so unlabelled.
 */
function ticks(min: number, max: number): { values: number[]; lo: number; hi: number } {
  const span = Math.max(max - min, 1)
  const rough = span / 4
  const magnitude = 10 ** Math.floor(Math.log10(rough))
  const step = [1, 2, 5, 10].map((m) => m * magnitude).find((s) => s >= rough) ?? magnitude * 10

  const lo = Math.floor(min / step) * step
  const hi = Math.ceil(max / step) * step

  const values: number[] = []
  // Guarded: a step that cannot advance the loop would hang the render, and a chart is not worth
  // a frozen tab.
  for (let v = lo; v <= hi + step / 2 && values.length < 12 && step > 0; v += step) values.push(v)

  return { values, lo, hi: hi > lo ? hi : lo + step }
}

function Tooltip({ x, width, children }: { x: number; width: number; children: React.ReactNode }) {
  // Flipped rather than clipped near the right edge: a tooltip that runs off the card is a
  // tooltip that cannot be read at exactly the point somebody is inspecting.
  const flip = x > width - 140

  return (
    <div
      className="pointer-events-none absolute z-10 rounded-md border bg-popover px-2 py-1 text-popover-foreground shadow-md"
      style={{
        left: flip ? undefined : x + 10,
        right: flip ? width - x + 10 : undefined,
        top: 4,
        fontSize: 'var(--text-small)',
        borderWidth: 'var(--hairline)',
      }}
    >
      {children}
    </div>
  )
}

const PLOT_HEIGHT = 132

// Room for the widest tick label at VR type size ("-100" at 18px), not just at the dense 13px.
// The SVG is drawn in real pixels so that text keeps its token size, which means this gutter has
// to be sized for the largest density rather than the default one.
const PAD_LEFT = 52

const PAD_BOTTOM = 20
const PAD_TOP = 10

/**
 * One series over time, as a line with a wash under it.
 *
 * A line because the question is a shape — is this going up — and the end value is labelled
 * directly rather than every point, which is how direct labels stay readable.
 */
export function TimeSeriesChart({
  points,
  series = 1,
  valueLabel,
  zeroBased = true,
}: {
  points: Point[]
  series?: 1 | 2 | 3 | 4 | 5
  valueLabel: string
  /**
   * Whether the axis has to include zero.
   *
   * True for anything that is a quantity of events, where the distance from zero is the message.
   * False for a *level* — a membership of 14,208 that moved by 400 over three months is a flat
   * line against a zero axis, and the movement is the entire reason anyone opened the chart. A
   * fitted axis for a level is honest as long as the axis labels are read, which is why the tick
   * values stay on screen and the area wash is dropped: a filled region above a non-zero floor
   * reads as a quantity, and it is not one.
   */
  zeroBased?: boolean
}) {
  const [ref, width] = useWidth()
  const [hover, setHover] = useState<number | null>(null)

  const color = `var(--series-${series})`
  const plotWidth = Math.max(0, width - PAD_LEFT - 12)

  const onMove = useCallback(
    (e: React.MouseEvent<SVGSVGElement>) => {
      if (points.length === 0 || plotWidth <= 0) return
      const box = e.currentTarget.getBoundingClientRect()
      const x = e.clientX - box.left - PAD_LEFT
      const index = Math.round((x / plotWidth) * (points.length - 1))
      setHover(Math.min(points.length - 1, Math.max(0, index)))
    },
    [points.length, plotWidth],
  )

  if (points.length === 0) return <Empty ref={ref} />

  const values = points.map((p) => p.value)
  const scale = zeroBased
    ? ticks(Math.min(...values, 0), Math.max(...values, 0))
    : ticks(Math.min(...values), Math.max(...values))

  const x = (i: number) =>
    PAD_LEFT + (points.length === 1 ? plotWidth / 2 : (i / (points.length - 1)) * plotWidth)
  const y = (v: number) =>
    PAD_TOP + (1 - (v - scale.lo) / (scale.hi - scale.lo || 1)) * (PLOT_HEIGHT - PAD_TOP - PAD_BOTTOM)

  const path = points.map((p, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)},${y(p.value).toFixed(1)}`).join(' ')

  // The wash runs to zero, not to the bottom of the plot: on a series that crosses zero, filling
  // to the floor would shade the negative region as though it were positive area.
  const base = y(Math.min(Math.max(0, scale.lo), scale.hi))
  const last = points[points.length - 1]
  const active = hover === null ? null : points[hover]

  return (
    <div ref={ref} className="relative">
      {width > 0 && (
        <svg
          width={width}
          height={PLOT_HEIGHT}
          role="img"
          aria-label={`${valueLabel} over time`}
          onMouseMove={onMove}
          onMouseLeave={() => setHover(null)}
        >
          {scale.values.map((t) => (
            <g key={t}>
              <line
                x1={PAD_LEFT}
                x2={width - 12}
                y1={y(t)}
                y2={y(t)}
                stroke="var(--chart-grid)"
                strokeWidth="var(--hairline)"
              />
              <text
                x={PAD_LEFT - 6}
                y={y(t) + 3}
                textAnchor="end"
                fill="var(--muted-foreground)"
                style={{ fontSize: 'var(--text-small)' }}
              >
                {compact(t)}
              </text>
            </g>
          ))}

          {zeroBased && (
            <path d={`${path} L${x(points.length - 1)},${base} L${x(0)},${base} Z`} fill={color} opacity="0.1" />
          )}
          <path
            d={path}
            fill="none"
            stroke={color}
            strokeWidth="var(--chart-stroke)"
            strokeLinejoin="round"
            strokeLinecap="round"
          />

          {/* End marker with a surface ring, so it stays legible where it crosses the line. */}
          <circle cx={x(points.length - 1)} cy={y(last.value)} r="4" fill={color} stroke="var(--card)" strokeWidth="2" />

          {active && (
            <>
              <line
                x1={x(hover!)}
                x2={x(hover!)}
                y1={PAD_TOP}
                y2={PLOT_HEIGHT - PAD_BOTTOM}
                stroke="var(--chart-grid)"
                strokeWidth="var(--hairline)"
              />
              <circle cx={x(hover!)} cy={y(active.value)} r="4" fill={color} stroke="var(--card)" strokeWidth="2" />
            </>
          )}

          <text
            x={PAD_LEFT}
            y={PLOT_HEIGHT - 4}
            fill="var(--muted-foreground)"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {shortDay(points[0].day)}
          </text>
          <text
            x={width - 12}
            y={PLOT_HEIGHT - 4}
            textAnchor="end"
            fill="var(--muted-foreground)"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {shortDay(last.day)}
          </text>
        </svg>
      )}

      {active && (
        <Tooltip x={x(hover!)} width={width}>
          <div className="text-muted-foreground">{shortDay(active.day)}</div>
          <div className="font-medium tabular-nums">
            {compact(active.value)} <span className="text-muted-foreground">{valueLabel}</span>
          </div>
        </Tooltip>
      )}
    </div>
  )
}

/**
 * One series of daily counts, as columns.
 *
 * Columns rather than a line because these are counts of discrete events, and a line between two
 * days implies values in between that were never measured.
 */
export function ColumnChart({
  points,
  series = 1,
  valueLabel,
  height = PLOT_HEIGHT,
}: {
  points: Point[]
  series?: 1 | 2 | 3 | 4 | 5
  valueLabel: string
  height?: number
}) {
  const [ref, width] = useWidth()
  const [hover, setHover] = useState<number | null>(null)

  const color = `var(--series-${series})`

  if (points.length === 0) return <Empty ref={ref} />

  const max = Math.max(...points.map((p) => p.value), 1)
  const plotWidth = Math.max(0, width - PAD_LEFT - 12)
  const slot = plotWidth / points.length

  // Capped rather than filling the slot: the leftover is air, and a 2px gap in the surface colour
  // is what separates neighbours -- never a stroke drawn around the mark.
  const barWidth = Math.max(1, Math.min(24, slot - 2))
  const plotHeight = height - PAD_TOP - PAD_BOTTOM

  return (
    <div ref={ref} className="relative">
      {width > 0 && (
        <svg width={width} height={height} role="img" aria-label={`${valueLabel} per day`}>
          <line
            x1={PAD_LEFT}
            x2={width - 12}
            y1={PAD_TOP + plotHeight}
            y2={PAD_TOP + plotHeight}
            stroke="var(--chart-grid)"
            strokeWidth="var(--hairline)"
          />
          <text
            x={PAD_LEFT - 6}
            y={PAD_TOP + 4}
            textAnchor="end"
            fill="var(--muted-foreground)"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {compact(max)}
          </text>

          {points.map((p, i) => {
            const h = (p.value / max) * plotHeight
            const bx = PAD_LEFT + i * slot + (slot - barWidth) / 2

            return (
              <g key={p.day} onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)}>
                {/* A hit target the full slot wide: the bar itself is often one pixel. */}
                <rect x={PAD_LEFT + i * slot} y={PAD_TOP} width={slot} height={plotHeight} fill="transparent" />
                {p.value > 0 && (
                  <rect
                    x={bx}
                    y={PAD_TOP + plotHeight - h}
                    width={barWidth}
                    height={h}
                    rx={Math.min(4, barWidth / 2)}
                    fill={color}
                    opacity={hover === null || hover === i ? 1 : 0.55}
                  />
                )}
              </g>
            )
          })}

          <text
            x={PAD_LEFT}
            y={height - 4}
            fill="var(--muted-foreground)"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {shortDay(points[0].day)}
          </text>
          <text
            x={width - 12}
            y={height - 4}
            textAnchor="end"
            fill="var(--muted-foreground)"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {shortDay(points[points.length - 1].day)}
          </text>
        </svg>
      )}

      {hover !== null && (
        <Tooltip x={PAD_LEFT + hover * slot} width={width}>
          <div className="text-muted-foreground">{shortDay(points[hover].day)}</div>
          <div className="font-medium tabular-nums">
            {compact(points[hover].value)} <span className="text-muted-foreground">{valueLabel}</span>
          </div>
        </Tooltip>
      )}
    </div>
  )
}

/**
 * Ranked totals, as horizontal bars with the value at the tip.
 *
 * One colour for every bar. Shading them by size would burn the only free channel on information
 * the bar length already carries, and the value is labelled anyway.
 */
export function RankedBars({
  rows,
  series = 1,
}: {
  rows: { key: string; label: string; value: number }[]
  series?: 1 | 2 | 3 | 4 | 5
}) {
  const color = `var(--series-${series})`
  const max = Math.max(...rows.map((r) => r.value), 1)

  if (rows.length === 0) return null

  return (
    <div className="flex flex-col gap-1.5">
      {rows.map((row) => (
        <div key={row.key} className="flex items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
          <div className="w-32 shrink-0 truncate text-muted-foreground" title={row.label}>
            {row.label}
          </div>
          <div className="relative h-3 flex-1 overflow-hidden rounded-sm bg-secondary">
            <div
              className="h-full rounded-sm"
              style={{ width: `${Math.max(2, (row.value / max) * 100)}%`, background: color }}
            />
          </div>
          <div className="w-12 shrink-0 text-right font-medium tabular-nums">{compact(row.value)}</div>
        </div>
      ))}
    </div>
  )
}

/** Identity never rides on colour alone, so every multi-series view carries one of these. */
export function Legend({ items }: { items: { label: string; series: 1 | 2 | 3 | 4 | 5 }[] }) {
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
      {items.map((item) => (
        <span key={item.label} className="flex items-center gap-1.5 text-muted-foreground">
          <span
            className="size-2.5 shrink-0 rounded-full"
            style={{ background: `var(--series-${item.series})` }}
          />
          {item.label}
        </span>
      ))}
    </div>
  )
}

function Empty({ ref }: { ref: React.RefObject<HTMLDivElement | null> }) {
  return (
    <div
      ref={ref}
      className="grid text-muted-foreground"
      style={{ height: PLOT_HEIGHT, placeItems: 'center', fontSize: 'var(--text-small)' }}
    >
      Nothing recorded in this window.
    </div>
  )
}
