import { useCallback, useMemo } from 'react'
import { useLocation } from './router.ts'

/**
 * Where a list page is, kept in the address beside its filters.
 *
 * A list pages by cursor: the server hands back a piece of text naming the row the next page
 * starts after, and the browser sends it back. That piece of text is the page's position, and it
 * belongs in the address for the same reason the filter chips do (`filters.ts`) -- a moderator
 * who finds something on the third page of the ban list should be able to send somebody the
 * third page of the ban list, and should get it back when they refresh.
 *
 * It is written as a *pushed* history entry, not a replaced one, so Back walks back through the
 * pages that were turned. Filters replace instead, which is why changing a filter does not bury
 * the page under history -- and why turning a page does.
 *
 * The position is opaque here on purpose. The browser never reads a cursor or builds one; it
 * carries the server's text around and hands it back. When the server cannot read it -- an old
 * link, a list since sorted another way, a hand-edited address -- the answer is the first page,
 * not an error, so a stale link still shows the list.
 *
 * The pure parts are here without any DOM so they can be tested with Node alone.
 */

/** The query parameter the position travels in. */
export const PARAM = 'cursor'

/** The position a query string carries, or null when it says nothing -- the first page. */
export function readPosition(params: URLSearchParams): string | null {
  const value = params.get(PARAM)
  return value === null || value === '' ? null : value
}

/** The position written into a query string. Null clears it, leaving the rest alone. */
export function writePosition(params: URLSearchParams, cursor: string | null): void {
  if (cursor === null || cursor === '') params.delete(PARAM)
  else params.set(PARAM, cursor)
}

/** The address as it would be with this position, the rest of the query string untouched. */
export function positionHref(search: string, pathname: string, cursor: string | null): string {
  const params = new URLSearchParams(search)
  writePosition(params, cursor)

  const query = params.toString()
  return pathname + (query ? `?${query}` : '')
}

export type ListPosition = {
  /** What to send the server. Null on the first page. */
  cursor: string | null
  /** Turn to the page this cursor names. Pushes, so Back returns to the page being left. */
  turnTo: (cursor: string | null) => void
  /** Back to the first page, without a history entry -- for when the filters change under it. */
  restart: () => void
}

/**
 * The position of the list on this page, and the two ways it changes.
 *
 * Every list page keeps its position here rather than in a `page` of its own, so that turning a
 * page, sharing the link and pressing Back all mean the same thing on every list.
 */
export function useListPosition(): ListPosition {
  const [location, navigate] = useLocation()

  const cursor = useMemo(() => readPosition(location.search), [location.search])

  const turnTo = useCallback(
    (next: string | null) => {
      navigate(positionHref(window.location.search, window.location.pathname, next))
    },
    [navigate],
  )

  const restart = useCallback(() => {
    // A filter change is not a page turn, so it leaves no entry to press Back into. `navigate`
    // does nothing when the address is already this, so calling it on every filter change is free.
    navigate(positionHref(window.location.search, window.location.pathname, null), { replace: true })
  }, [navigate])

  return { cursor, turnTo, restart }
}
