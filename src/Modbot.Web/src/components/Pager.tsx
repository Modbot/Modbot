import { Button } from '@/components/ui/button'
import type { ListPosition } from '@/lib/listPosition'

/**
 * The two controls under a list that pages by cursor.
 *
 * Three pages had their own copy of this footer, each recomputing a page number from a total.
 * There is no page number to compute now: a cursor list knows the page before and the page after
 * and nothing else, so the controls say only that. The count of matching rows, where a list still
 * has one, belongs at the top of the list with the filters, not down here.
 */
export function Pager({ at, next, previous }: { at: ListPosition; next: string | null; previous: string | null }) {
  if (!next && !previous) return null

  return (
    <div
      className="flex items-center gap-2 border-t px-3 py-2"
      style={{ borderTopWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      <Button variant="outline" size="xs" disabled={!previous} onClick={() => at.turnTo(previous)}>
        Previous
      </Button>
      <Button variant="outline" size="xs" disabled={!next} onClick={() => at.turnTo(next)}>
        Next
      </Button>
    </div>
  )
}
