import { compactNumber } from './format'

export type TooltipRow = { name: string; value: number | string; color?: string }

/**
 * The one tooltip every chart uses, drawn in the popover tokens so it matches the rest of the
 * app in both themes. Recharts' default tooltip ships its own white box and its own font, which
 * is the first thing that looks foreign on a dark screen.
 *
 * Identity never rides on colour alone: each row carries its name in text, and the swatch is a
 * reminder rather than the label.
 */
export function ChartTooltip({
  title,
  rows,
  format = compactNumber,
}: {
  title: string
  rows: TooltipRow[]
  format?: (value: number) => string
}) {
  return (
    <div
      className="rounded-xl border bg-popover px-2 py-1 text-popover-foreground shadow-md"
      style={{ fontSize: 'var(--text-small)', borderWidth: 'var(--hairline)' }}
    >
      <div className="text-muted-foreground">{title}</div>
      {rows.map((row) => (
        <div key={row.name} className="flex items-center gap-1.5 tabular-nums">
          {row.color && <span className="size-2 shrink-0 rounded-full" style={{ background: row.color }} />}
          <span className="font-medium">{typeof row.value === 'number' ? format(row.value) : row.value}</span>
          <span className="text-muted-foreground">{row.name}</span>
        </div>
      ))}
    </div>
  )
}
