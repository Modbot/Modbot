import { Card } from '@/components/ui/card'
import { EmptyRow, type RowState } from '@/components/PanelGrid'

/**
 * Pieces every list page draws the same way. They lived in the Members page until it became a
 * view of People (2026-09-27), because it was the first list and every later one borrowed them.
 */

/**
 * The marks after a name in a list's first column: 18+, the trust rank, "representing".
 *
 * On a phone that column is pinned and capped, so the name keeps its line and the marks take what
 * is left of it. A mark that does not fit whole goes to a second line the box cuts off, so a row is
 * always one height and never shows half a badge; the popup the row opens lists them all. The box
 * is one badge high: the badge's line of `--text-small` at 1.35, its 1px of padding above and
 * below and its two hairlines. From `md` up the box is not drawn and the marks sit on the name's
 * line.
 */
export function Marks({ children }: { children: React.ReactNode }) {
  return (
    <span
      className="flex min-w-0 flex-wrap items-center gap-1.5 overflow-hidden md:contents"
      style={{ height: 'calc(var(--text-small) * 1.35 + 2px + 2 * var(--hairline))' }}
    >
      {/* Holds the first line, so a first mark too wide for it goes down with the rest. */}
      <span aria-hidden className="-mr-1.5 h-full w-0 md:hidden" />
      {children}
    </span>
  )
}

/**
 * A page that has no list to show yet: `loading`, or `danger` when the list could not be read,
 * with its "Try again" (`EmptyRow`). The one page-level message: the analytics pages' `PageMessage`
 * and the settings sections' `Placeholder` are this, so the three can never drift apart.
 */
export function Empty({ className, ...row }: RowState & { className?: string }) {
  return (
    <Card className={className}>
      <EmptyRow lines={3} {...row} />
    </Card>
  )
}
