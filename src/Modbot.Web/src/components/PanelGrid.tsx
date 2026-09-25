import { cn } from '@/lib/utils'

/**
 * Panels that sit side by side, sharing one hairline instead of standing apart with gaps.
 *
 * The columns are the caller's (`grid-cols-2 xl:grid-cols-4`, `lg:grid-cols-2`, `grid-cols-12`);
 * the lines are index.css's, which strips each child's own border and gives it an outline that
 * merges with its neighbour's. Any child works: a Card, a Stat, a SettingsCard, a chart panel.
 */
export function PanelGrid({
  id,
  as: Tag = 'div',
  className,
  children,
}: {
  /** An anchor to scroll to, such as Health's `#ai`. */
  id?: string
  /** `ul` or `ol` when the panels are the items of a list. */
  as?: 'div' | 'ul' | 'ol'
  className?: string
  children: React.ReactNode
}) {
  return (
    <Tag id={id} data-slot="panel-grid" className={className}>
      {children}
    </Tag>
  )
}

/**
 * What a panel shows when it has nothing to show: one line, where the first row would have been.
 *
 * Left-aligned and a row high, so an empty list still reads as a list that happens to be empty
 * rather than as a message in the middle of a box. The hollow square is the console's "no signal"
 * indicator, the unfilled twin of the filled squares that mark a status. `danger` is for a list
 * that could not be read at all: the square fills in the destructive colour and the words, as
 * after every filled square, are plain foreground text.
 */
export function EmptyRow({
  children,
  className,
  tone = 'neutral',
  minHeight = 'calc(var(--row-h) * 1.5)',
}: {
  children: React.ReactNode
  className?: string
  /** `danger` when the row stands in for a load that failed, not for a list that is empty. */
  tone?: 'neutral' | 'danger'
  /** Kept for a chart panel whose height should not jump when data arrives. */
  minHeight?: number | string
}) {
  return (
    <div
      data-slot="empty-row"
      className={cn(
        'flex items-center gap-2 px-(--panel-pad)',
        tone === 'danger' ? 'text-foreground' : 'text-muted-foreground',
        className,
      )}
      style={{ minHeight, fontSize: 'var(--text-small)' }}
    >
      <span
        aria-hidden
        className={cn(
          'size-2 shrink-0',
          tone === 'danger' ? 'bg-destructive' : 'border border-current opacity-70',
        )}
      />
      <span className="min-w-0">{children}</span>
    </div>
  )
}
