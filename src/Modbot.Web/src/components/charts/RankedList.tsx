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

/** Identity never rides on colour alone, so every multi-series view carries one of these. */
export function Legend({ items }: { items: { label: string; slot: SeriesSlot }[] }) {
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
      {items.map((item) => (
        <span key={item.label} className="flex items-center gap-1.5 text-muted-foreground">
          <span className="size-2.5 shrink-0 rounded-full" style={{ background: seriesColor(item.slot) }} />
          {item.label}
        </span>
      ))}
    </div>
  )
}
