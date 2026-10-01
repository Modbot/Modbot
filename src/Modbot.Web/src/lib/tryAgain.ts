import { createContext, useContext } from 'react'

/**
 * What "Try again" does on a failed row that was not handed a way to load its own thing again.
 *
 * Every failed load has a "Try again" (`EmptyRow`). Most rows are handed the read that failed
 * (`useLoad`'s `reload`, or a section's own `load`) and run just that. A row that is not falls
 * back on the area it sits in (`TryAgainArea`): the page, or the popup. Trying again there draws
 * the area afresh, which runs every read in it again from the start, the same as opening it again.
 *
 * That is the right answer for a page whose one read failed and left nothing else on screen, and a
 * safe one everywhere else: what the page keeps in the address (filters, the page of a list, the
 * open tab) survives it. Anything kept only in memory does not, which is why a part that holds a
 * half-written form beside a failed read is handed its own reload instead.
 *
 * Outside every area (a sign-in screen, the command palette) the fallback reloads the app, which
 * is what a moderator would have had to do by hand.
 */
export const TryAgainContext = createContext<() => void>(() => window.location.reload())

/** The page's or popup's own "draw it again", for a failed row with no reload of its own. */
export function useTryAgain(): () => void {
  return useContext(TryAgainContext)
}
