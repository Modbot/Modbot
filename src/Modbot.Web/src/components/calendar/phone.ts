import { useSyncExternalStore } from 'react'

/** Below Tailwind's `sm`: a phone held upright, where seven columns of hours do not fit. */
export const NARROW = '(max-width: 39.9375rem)'

/**
 * Where an event or the New event form opens as a sheet from the bottom rather than beside the
 * tap: a phone held upright, and a touch screen short of a desk's width, which covers a phone held
 * sideways. The second half is the phone rule in `index.css`.
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

/**
 * The classes that turn a `DialogContent` into a sheet along the bottom of the screen: the whole
 * width, square at the bottom, and never more than most of the screen tall, so the page it came
 * from still shows above it.
 */
export const SHEET_CLASS =
  'top-auto bottom-0 left-0 max-h-[85dvh] w-full max-w-none translate-x-0 translate-y-0 rounded-b-none border-x-0 border-b-0 [&_h2]:whitespace-normal [&_h2]:[overflow-wrap:anywhere]'

/** A sheet's body when nothing is pinned under it: clear of a phone's home bar, as the buttons are. */
export const SHEET_LAST_ROW_CLASS = 'pb-[max(1rem,env(safe-area-inset-bottom))]'

/** The sheet's pinned row of buttons, clear of a phone's home bar. */
export const SHEET_FOOT_CLASS =
  'flex shrink-0 flex-wrap gap-2 border-t border-t-(length:--hairline) px-4 pt-3 pb-[max(0.75rem,env(safe-area-inset-bottom))] *:grow'
