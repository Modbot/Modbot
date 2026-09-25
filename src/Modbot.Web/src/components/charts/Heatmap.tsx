import { useLayoutEffect, useRef, useState } from 'react'
import { compactNumber } from './format'
import { seriesColor, type SeriesSlot } from './theme'

/**
 * A grid of values, shaded by size -- the hour-of-week chart, and anything else shaped like a
 * table where the eye should find the hot spots before reading the numbers.
 *
 * Drawn with plain elements rather than Recharts, which has no heatmap. Shade is opacity over a
 * single series colour, so it is theme-correct for free and never uses hue to carry the value;
 * the exact number is in the tooltip and read out by the cell's title, so nothing depends on
 * telling two shades apart.
 */
export function Heatmap({
  rows,
  cols,
  values,
  valueLabel,
  slot = 1,
  colLabelEvery = 3,
}: {
  rows: string[]
  cols: string[]
  /** `values[row][col]`. */
  values: number[][]
  valueLabel: string
  slot?: SeriesSlot
  /** Show every nth column label, so 24 hours do not become an unreadable strip. */
  colLabelEvery?: number
}) {
  const [hover, setHover] = useState<{ r: number; c: number } | null>(null)
  const max = Math.max(1, ...values.flat())
  const color = seriesColor(slot)
  const { grid: gridRef, label: labelRef, n: labelEvery } = useLabelEvery(colLabelEvery, cols.length)

  return (
    <div className="relative" style={{ fontSize: 'var(--text-small)' }}>
      <span ref={labelRef} aria-hidden className="invisible absolute whitespace-nowrap">
        {cols.reduce((a, b) => (b.length > a.length ? b : a), '')}
      </span>
      <div
        ref={gridRef}
        className="grid gap-px"
        style={{ gridTemplateColumns: `3.5rem repeat(${cols.length}, minmax(0, 1fr))` }}
        onMouseLeave={() => setHover(null)}
      >
        <div />
        {/* Each label spans the columns up to the next one and is clipped there, so a label never
            runs into its neighbour however narrow the columns get. */}
        {cols.map((c, i) =>
          i % labelEvery === 0 ? (
            <div
              key={c}
              className="overflow-hidden whitespace-nowrap text-muted-foreground"
              style={{ minHeight: '1.25rem', gridColumn: `span ${Math.min(labelEvery, cols.length - i)}` }}
            >
              {c}
            </div>
          ) : null,
        )}

        {rows.map((r, ri) => (
          <RowCells
            key={r}
            label={r}
            ri={ri}
            cells={values[ri] ?? []}
            max={max}
            color={color}
            hover={hover}
            onHover={setHover}
          />
        ))}
      </div>

      {hover && (
        <div className="mt-2 text-muted-foreground">
          <span className="font-mono font-medium text-foreground">
            {compactNumber(values[hover.r]?.[hover.c] ?? 0)}
          </span>{' '}
          {valueLabel} · {rows[hover.r]} {cols[hover.c]}
        </div>
      )}
    </div>
  )
}

function RowCells({
  label,
  ri,
  cells,
  max,
  color,
  hover,
  onHover,
}: {
  label: string
  ri: number
  cells: number[]
  max: number
  color: string
  hover: { r: number; c: number } | null
  onHover: (h: { r: number; c: number }) => void
}) {
  return (
    <>
      <div className="truncate pr-2 text-right text-muted-foreground" style={{ lineHeight: '1.25rem' }}>
        {label}
      </div>
      {cells.map((v, ci) => {
        const active = hover?.r === ri && hover?.c === ci
        // A floor of 0.08 keeps a non-zero cell visible against the surface; zero stays blank so
        // "nothing happened" reads as nothing rather than as a faint something.
        const opacity = v <= 0 ? 0 : 0.08 + 0.92 * (v / max)

        return (
          <div
            key={ci}
            role="img"
            aria-label={`${label} ${ci}: ${v}`}
            title={`${v}`}
            onMouseEnter={() => onHover({ r: ri, c: ci })}
            className="bg-secondary"
            style={{ height: '1.25rem', outline: active ? `2px solid ${color}` : undefined, outlineOffset: -1 }}
          >
            <div className="h-full w-full" style={{ background: color, opacity }} />
          </div>
        )
      })}
    </>
  )
}

/**
 * How many columns each label covers: `colLabelEvery`, doubled until a label fits in the width it
 * spans, so a phone shows 0:00, 6:00, 12:00 and 18:00 where a desktop shows every third hour.
 */
function useLabelEvery(colLabelEvery: number, colCount: number) {
  const grid = useRef<HTMLDivElement>(null)
  const label = useRef<HTMLSpanElement>(null)
  const [n, setN] = useState(colLabelEvery)

  useLayoutEffect(() => {
    const el = grid.current
    if (!el) return

    const measure = () => {
      const cells = el.children
      // The corner cell sets the row labels' width; the rest of the row is the columns and their gaps.
      const corner = cells[0]?.getBoundingClientRect().width ?? 0
      const gap = parseFloat(getComputedStyle(el).columnGap) || 0
      const col = (el.clientWidth - corner - gap * colCount) / Math.max(1, colCount)
      // Room for the longest label plus a little air before the next.
      const need = (label.current?.getBoundingClientRect().width ?? 0) * 1.25

      let next = colLabelEvery
      while (next < colCount && next * col + (next - 1) * gap < need) next *= 2
      setN(next)
    }

    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(el)
    return () => observer.disconnect()
  }, [colLabelEvery, colCount])

  return { grid, label, n }
}
