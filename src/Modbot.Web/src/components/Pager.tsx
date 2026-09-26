import { useEffect } from 'react'
import { Button } from '@/components/ui/button'
import { GAP, pageNumbers, type ListPage } from '@/lib/listPage'

/**
 * The numbers under a list, and the two steps either side of them.
 *
 * Every list page had its own copy of this footer, each recomputing the number of pages from a
 * total and each drawing Previous and Next with a sentence between them. They are one control
 * now, so a page turn looks and behaves the same on every list.
 *
 * A long list shows the ends and the page being read with its neighbours, with gaps for the rest
 * (`lib/listPage.ts`): a hundred numbers in a row is a wall, not a control.
 *
 * @param at Where the list is: `useListPage()` for a page's own list, whose page is in the address,
 *   or `usePageState()` for a list inside a popup or a panel, which keeps its page to itself.
 * @param pages How many pages there are, from the total and the page size.
 */
export function Pager({ at, pages }: { at: ListPage; pages: number }) {
  const { page, goTo, restart } = at

  // A filter narrowing under somebody on page nine leaves them past the end of the list, looking
  // at nothing, with no number left to press to get back.
  useEffect(() => {
    if (page > pages) restart()
  }, [page, pages, restart])

  if (pages <= 1) return null

  return (
    <nav
      aria-label="Pages"
      className="flex min-h-(--strip-h) flex-wrap items-center gap-1 border-t border-t-(length:--hairline) bg-strip px-(--panel-pad) py-1"
      style={{ fontSize: 'var(--text-small)' }}
    >
      <Button variant="outline" size="xs" disabled={page <= 1} onClick={() => goTo(page - 1)}>
        Previous
      </Button>

      {pageNumbers(page, pages).map((slot, i) =>
        slot === GAP ? (
          <span key={`gap-${i}`} aria-hidden="true" className="px-1 text-muted-foreground">
            …
          </span>
        ) : (
          <Button
            key={slot}
            variant="ghost"
            className={slot === page ? 'bg-accent font-mono text-accent-foreground hover:bg-accent hover:text-accent-foreground' : 'font-mono'}
            size="xs"
            aria-label={`Page ${slot}`}
            aria-current={slot === page ? 'page' : undefined}
            onClick={() => goTo(slot)}
          >
            {slot.toLocaleString()}
          </Button>
        ),
      )}

      <Button variant="outline" size="xs" disabled={page >= pages} onClick={() => goTo(page + 1)}>
        Next
      </Button>
    </nav>
  )
}
