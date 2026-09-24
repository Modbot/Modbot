import { cn } from '@/lib/utils'

/**
 * Panels that sit side by side, sharing one hairline instead of standing apart with gaps.
 *
 * The columns are the caller's (`grid-cols-2 xl:grid-cols-4`, `lg:grid-cols-2`, `grid-cols-12`);
 * the lines are index.css's, which strips each child's own border and gives it an outline that
 * merges with its neighbour's. Any child works: a Card, a Stat, a SettingsCard, a chart panel.
 */
export function PanelGrid({ className, children }: { className?: string; children: React.ReactNode }) {
  return (
    <div data-slot="panel-grid" className={className}>
      {children}
    </div>
  )
}

/**
 * What a panel shows when it has nothing to show: one line, where the first row would have been.
 *
 * Left-aligned and a row high, so an empty list still reads as a list that happens to be empty
 * rather than as a message in the middle of a box. The hollow square is the console's "no signal"
 * indicator, the unfilled twin of the filled squares that mark a status.
 */
export function EmptyRow({
  children,
  className,
  minHeight = 'calc(var(--row-h) * 1.5)',
}: {
  children: React.ReactNode
  className?: string
  /** Kept for a chart panel whose height should not jump when data arrives. */
  minHeight?: number | string
}) {
  return (
    <div
      data-slot="empty-row"
      className={cn('flex items-center gap-2 px-(--panel-pad) text-muted-foreground', className)}
      style={{ minHeight, fontSize: 'var(--text-small)' }}
    >
      <span aria-hidden className="size-2 shrink-0 border border-current opacity-70" />
      <span className="min-w-0">{children}</span>
    </div>
  )
}
