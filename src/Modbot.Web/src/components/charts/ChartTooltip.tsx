/**
 * The frame every recharts tooltip renders into, so they all look like the app's popovers
 * rather than like recharts' default white box. Pass one of these from a chart's
 * `<Tooltip content={...}>` with whatever rows that chart wants to show.
 *
 * Values are written in text tokens, never in the series colour — a coloured dot beside the
 * label is what carries identity when a chart has more than one series.
 */
export function ChartTooltipFrame({
  title,
  children,
}: {
  title: React.ReactNode
  children: React.ReactNode
}) {
  return (
    <div
      className="rounded-md border bg-popover px-2 py-1 text-popover-foreground shadow-md"
      style={{ fontSize: 'var(--text-small)', borderWidth: 'var(--hairline)' }}
    >
      <div className="text-muted-foreground">{title}</div>
      {children}
    </div>
  )
}

/** One labelled value inside the frame. */
export function ChartTooltipRow({
  label,
  value,
  series,
}: {
  label?: React.ReactNode
  value: React.ReactNode
  /** Series colour for the identity dot, when the chart has more than one series. */
  series?: string
}) {
  return (
    <div className="flex items-baseline gap-1.5 tabular-nums">
      {series && <span className="size-2 shrink-0 rounded-full" style={{ background: series }} />}
      <span className="font-medium">{value}</span>
      {label && <span className="text-muted-foreground">{label}</span>}
    </div>
  )
}
