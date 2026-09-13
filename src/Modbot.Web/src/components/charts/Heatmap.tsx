import { useState } from 'react'
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

  return (
    <div className="relative" style={{ fontSize: 'var(--text-small)' }}>
      <div
        className="grid gap-px"
        style={{ gridTemplateColumns: `3.5rem repeat(${cols.length}, minmax(0, 1fr))` }}
        onMouseLeave={() => setHover(null)}
      >
        <div />
        {cols.map((c, i) => (
          <div key={c} className="truncate text-center text-muted-foreground" style={{ minHeight: '1.25rem' }}>
            {i % colLabelEvery === 0 ? c : ''}
          </div>
        ))}

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
          <span className="font-medium text-foreground tabular-nums">
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
            className="rounded-[2px] bg-secondary"
            style={{ height: '1.25rem', outline: active ? `2px solid ${color}` : undefined, outlineOffset: -1 }}
          >
            <div className="h-full w-full rounded-[2px]" style={{ background: color, opacity }} />
          </div>
        )
      })}
    </>
  )
}
