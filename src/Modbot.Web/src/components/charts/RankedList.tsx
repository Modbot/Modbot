import { compactNumber } from './format'
import { seriesColor, type SeriesSlot } from './theme'

/**
 * Ranked totals, as horizontal bars with the value at the tip.
 *
 * Plain elements rather than a Recharts bar chart: a ranked list is a table with a bar in it,
 * and the labels want to be real text -- selectable, truncated with a title, laid out by CSS --
 * not SVG. One colour for every bar; shading by size would burn the only free channel on
 * information the bar length already carries, and the value is labelled anyway.
 */
export function RankedList({
  rows,
  slot = 1,
  format = compactNumber,
  onPick,
}: {
  rows: { key: string; label: string; value: number; note?: string }[]
  slot?: SeriesSlot
  format?: (value: number) => string
  /** Makes each row a button -- a list of people is a list of launchers for the subject pane. */
  onPick?: (key: string) => void
}) {
  const color = seriesColor(slot)
  const max = Math.max(...rows.map((r) => r.value), 1)

  if (rows.length === 0) return null

  return (
    <div className="flex flex-col gap-1.5">
      {rows.map((row) => {
        const label = (
          <div className="w-36 shrink-0 truncate text-muted-foreground" title={row.label}>
            {row.label}
          </div>
        )

        return (
          <div key={row.key} className="flex items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
            {onPick ? (
              <button type="button" className="w-36 shrink-0 truncate text-left hover:underline" onClick={() => onPick(row.key)}>
                {label}
              </button>
            ) : (
              label
            )}
            <div className="relative h-3 flex-1 overflow-hidden bg-secondary">
              <div className="h-full" style={{ width: `${Math.max(2, (row.value / max) * 100)}%`, background: color }} />
            </div>
            <div className="w-16 shrink-0 text-right font-mono font-medium">{format(row.value)}</div>
            {row.note && <div className="w-28 shrink-0 truncate text-muted-foreground" title={row.note}>{row.note}</div>}
          </div>
        )
      })}
    </div>
  )
}

/**
 * How a legend item draws its key. `dot` names a series by its colour; the others copy the mark
 * the chart draws, for a chart whose lines differ by how they are drawn as well as by colour: a
 * solid `line`, a `dashed` one, a shaded `band` and a `hairline` like a reference line.
 */
export type LegendSample = 'dot' | 'line' | 'dashed' | 'band' | 'hairline'

/**
 * One key in a legend. The colour is a series slot, or `color` for a mark drawn in another
 * token (`chartTheme.ok` for a line that means "online"). `value` is a reading set beside the
 * name in mono, for a chart that says each line's latest value in its key.
 */
export type LegendItem = {
  label: string
  value?: React.ReactNode
  sample?: LegendSample
} & ({ slot: SeriesSlot; color?: never } | { color: string; slot?: never })

function Sample({ sample = 'dot', color }: { sample?: LegendSample; color: string }) {
  switch (sample) {
    case 'line':
      return <span className="h-0.5 w-4 shrink-0 rounded-full" style={{ background: color }} />
    case 'dashed':
      return <span className="w-4 shrink-0 border-t-2 border-dashed" style={{ borderColor: color }} />
    case 'band':
      return <span className="h-2.5 w-4 shrink-0 rounded-sm" style={{ background: color, opacity: 0.25 }} />
    case 'hairline':
      return <span className="w-4 shrink-0 border-t border-t-(length:--hairline)" style={{ borderColor: color }} />
    default:
      return <span className="size-2.5 shrink-0 rounded-full" style={{ background: color }} />
  }
}

/** Identity never rides on colour alone, so every multi-series view carries one of these. */
export function Legend({ items }: { items: LegendItem[] }) {
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
      {items.map((item) => (
        <span key={item.label} className="flex items-center gap-1.5 text-muted-foreground">
          <Sample sample={item.sample} color={item.color ?? seriesColor(item.slot!)} />
          {item.label}
          {item.value !== undefined && (
            <span className="font-mono font-medium text-foreground" style={{ fontSize: 'var(--text-base)' }}>
              {item.value}
            </span>
          )}
        </span>
      ))}
    </div>
  )
}
