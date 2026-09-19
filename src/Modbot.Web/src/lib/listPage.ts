import { useCallback, useMemo } from 'react'
import { useLocation } from './router.ts'

/**
 * Which page of a list is being shown, kept in the address beside its filters.
 *
 * A list pages by number: page three is the third fifty rows of the list the filters describe.
 * That number belongs in the address for the same reason the filter chips do (`filters.ts`) -- a
 * moderator who finds something on the third page of the ban list should be able to send somebody
 * the third page of the ban list, and should get it back when they refresh.
 *
 * It is written as a *pushed* history entry, not a replaced one, so Back walks back through the
 * pages that were turned. Filters replace instead, which is why changing a filter does not bury
 * the page under history -- and why turning a page does.
 *
 * Anything that is not a whole page number reads as page one: a hand-edited address, a link from
 * an older build, `page=0`. A saved link should show the list rather than an error.
 *
 * Every list page keeps its page here rather than in a `useState` of its own, so that turning a
 * page, sharing the link and pressing Back mean the same thing on every list.
 *
 * The pure parts are here without any DOM so they can be tested with Node alone.
 */

/** The query parameter the page number travels in. */
export const PARAM = 'page'

/**
 * The page a query string asks for. One when it says nothing, or says something unusable.
 *
 * Only plain digits count. `Number` would read `1e3` as a thousand and ` 4 ` as four, and neither
 * is something this ever writes, so a hand-edited address means what it looks like or means one.
 */
export function readPage(params: URLSearchParams): number {
  const raw = params.get(PARAM)
  if (raw === null || !/^\d+$/.test(raw)) return 1

  const asked = Number(raw)
  return Number.isSafeInteger(asked) && asked >= 1 ? asked : 1
}

/** The page written into a query string. Page one is written as nothing, leaving the rest alone. */
export function writePage(params: URLSearchParams, page: number): void {
  if (!Number.isInteger(page) || page <= 1) params.delete(PARAM)
  else params.set(PARAM, String(page))
}

/** The address as it would be on this page, the rest of the query string untouched. */
export function pageHref(search: string, pathname: string, page: number): string {
  const params = new URLSearchParams(search)
  writePage(params, page)

  const query = params.toString()
  return pathname + (query ? `?${query}` : '')
}

/** Stands in the run of page numbers for the ones left out. */
export const GAP = 'gap'

export type PageSlot = number | typeof GAP

/**
 * The page numbers to draw, with gaps where numbers were left out.
 *
 * A list of two hundred pages cannot show two hundred numbers, so it shows the ends, the page
 * being read and its neighbours: `1 … 7 8 9 10 11 … 200`. The first and last are always there
 * because "start again" and "the newest" are the two jumps people actually make; the neighbours
 * are there because the page after the one you are on is the next thing you want.
 *
 * A gap standing for a single number is replaced by the number itself -- `1 2 3 4 5` rather than
 * `1 … 3 4 5` -- because it is no wider and one more page to reach.
 */
export function pageNumbers(page: number, pages: number, around = 2): PageSlot[] {
  const last = Math.max(1, Math.trunc(pages))
  const here = Math.min(Math.max(1, Math.trunc(page)), last)

  const wanted = new Set([1, last])
  for (let n = here - around; n <= here + around; n++) if (n >= 1 && n <= last) wanted.add(n)

  const slots: PageSlot[] = []
  for (const n of [...wanted].sort((a, b) => a - b)) {
    const before = slots.at(-1)
    if (typeof before === 'number') {
      if (n - before === 2) slots.push(before + 1)
      else if (n - before > 2) slots.push(GAP)
    }
    slots.push(n)
  }

  return slots
}

export type ListPage = {
  /** The page being read, counting from one. */
  page: number
  /** Turn to a page. Pushes, so Back returns to the page being left. */
  goTo: (page: number) => void
  /** Back to page one, without a history entry -- for when the filters change under it. */
  restart: () => void
}

/** The page this list is on, and the two ways it changes. */
export function useListPage(): ListPage {
  const [location, navigate] = useLocation()

  const page = useMemo(() => readPage(location.search), [location.search])

  const goTo = useCallback(
    (next: number) => {
      navigate(pageHref(window.location.search, window.location.pathname, next))
    },
    [navigate],
  )

  const restart = useCallback(() => {
    // A filter change is not a page turn, so it leaves no entry to press Back into. `navigate`
    // does nothing when the address is already this, so calling it on every filter change is free.
    navigate(pageHref(window.location.search, window.location.pathname, 1), { replace: true })
  }, [navigate])

  return { page, goTo, restart }
}
