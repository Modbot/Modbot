import { useSyncExternalStore } from 'react'

/** Below Tailwind's `sm`: a phone held upright, where seven columns of hours do not fit. */
export const NARROW = '(max-width: 39.9375rem)'

/**
 * Where an event or the New event form opens as a sheet from the bottom rather than beside the
 * tap: a phone held upright, and a touch screen short of a desk's width, which covers a phone held
 * sideways. The second half is the phone rule in `index.css`, and the whole of it is the `sheet`
 * variant there, which turns the `DialogContent` itself into the sheet: the two must agree, or an
 * event would open as a centered dialog where a popover beside the tap did not fit.
 */
export const SHEET = `${NARROW}, (pointer: coarse) and (max-width: 63.9375rem)`

/** Whether a media query matches now, kept up to date as the window turns or is resized. */
export function useMedia(query: string): boolean {
  return useSyncExternalStore(
    (changed) => {
      const list = window.matchMedia(query)
      list.addEventListener('change', changed)
      return () => list.removeEventListener('change', changed)
    },
    () => window.matchMedia(query).matches,
    () => false,
  )
}
