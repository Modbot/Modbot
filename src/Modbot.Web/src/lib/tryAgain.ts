import { createContext, useContext } from 'react'
import { ApiError } from '@/lib/api'

/**
 * What "Try again" does on a failed row that was not handed a way to load its own thing again.
 *
 * Every failed load has a "Try again" (`EmptyRow`), and every row is handed the read that failed
 * (`useLoad`'s `reload`, or a section's own `load`) and runs just that. This is the safety net for
 * a row that is not: it falls back on the area it sits in (`TryAgainArea`), the page or the popup.
 * Trying again there draws the area afresh, which runs every read in it again from the start, the
 * same as opening it again. What the page keeps in the address (filters, the page of a list, the
 * open tab) survives that; anything kept only in memory (a draft, a typed search) does not, which
 * is why no row should rely on it.
 *
 * Outside every area (a sign-in screen, the command palette) the fallback reloads the app, which
 * is what a moderator would have had to do by hand.
 */
export const TryAgainContext = createContext<() => void>(() => window.location.reload())

/** The page's or popup's own "draw it again", for a failed row with no reload of its own. */
export function useTryAgain(): () => void {
  return useContext(TryAgainContext)
}

/**
 * Whether a failed read is an answer rather than a failure: no permission (403) or nothing there
 * (404). Reading again cannot change either, so the row says so without a "Try again".
 */
export function isFinal(e: unknown): boolean {
  return e instanceof ApiError && (e.status === 403 || e.status === 404)
}
