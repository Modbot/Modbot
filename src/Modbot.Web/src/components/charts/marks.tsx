import { ReferenceArea, type LabelProps } from 'recharts'

/**
 * The marks a chart draws for days it has no data for, and for a day that is not over yet. Shared
 * by every chart, so a missing day looks the same on every page.
 *
 * The cue is the shape, never the colour: diagonal stripes in the muted tokens, recessive enough to
 * sit behind the series and unlike anything a series draws, and a hollow point for an unfinished
 * day. Both read the same to someone who cannot tell the colours apart. No colour here is anything
 * but a token.
 */

/** The stripe pattern, as a chart child. The stripe follows the hairline, so it stays visible in VR. */
export function Stripes({ id }: { id: string }) {
  return (
    <defs>
      <pattern id={id} patternUnits="userSpaceOnUse" width={6} height={6} patternTransform="rotate(45)">
        <rect width={6} height={6} style={{ fill: 'var(--muted)' }} />
        <line
          x1={1}
          y1={0}
          x2={1}
          y2={6}
          style={{ stroke: 'var(--muted-foreground)', strokeOpacity: 0.3, strokeWidth: 'calc(var(--hairline) * 2)' }}
        />
      </pattern>
    </defs>
  )
}

/** Wide enough for the words at every density, VR's larger text included. */
const LABEL_FITS = 64

/** "no data" along the top of a band, where it fits; a narrow band is left to its stripes. */
function NoDataLabel({ viewBox }: LabelProps) {
  if (!viewBox || !('width' in viewBox)) return null

  const { x = 0, y = 0, width = 0 } = viewBox
  if (width < LABEL_FITS) return null

  return (
    <text x={x + width / 2} y={y + 4} textAnchor="middle" dominantBaseline="hanging" className="recharts-text">
      no data
    </text>
  )
}

/**
 * One striped band per run of missing days, behind the series. `x1` and `x2` are axis values: the
 * first and last day on a day axis, or the run's start and end on a time axis. A chart with more
 * than one Y axis names the one to measure against, or the band has no height to draw.
 */
export function MissingBands({
  bands,
  stripeId,
  yAxisId,
}: {
  bands: { x1: number | string; x2: number | string }[]
  stripeId: string
  yAxisId?: string
}) {
  return (
    <>
      {bands.map((b) => (
        <ReferenceArea
          key={`${b.x1}-${b.x2}`}
          x1={b.x1}
          x2={b.x2}
          yAxisId={yAxisId}
          fill={`url(#${stripeId})`}
          fillOpacity={1}
          stroke="none"
          ifOverflow="hidden"
          label={NoDataLabel}
        />
      ))}
    </>
  )
}

/** A point on a line for a day that is not over yet: hollow, so it reads as unfinished. */
export function HollowDot({ cx, cy, color }: { cx?: number; cy?: number; color: string }) {
  if (cx === undefined || cy === undefined) return null
  return <circle cx={cx} cy={cy} r={3.5} style={{ fill: 'var(--card)', stroke: color, strokeWidth: 'var(--chart-stroke)' }} />
}
