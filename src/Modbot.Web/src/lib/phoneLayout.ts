import { useSyncExternalStore } from 'react'

/**
 * A screen that gets a popup's one-column phone layout: narrower than `md`, or a touch screen
 * under 500px tall, which is a phone on its side.
 *
 * The exact opposite of the `big` variant in `index.css`, which the popup's classes use. Kept as
 * one string here so the script and the styles cannot disagree about which layout is showing.
 */
export const PHONE_LAYOUT = '(width < 48rem), (pointer: coarse) and (height <= 31.25rem)'

export function isPhoneLayout(): boolean {
  return typeof window !== 'undefined' && window.matchMedia(PHONE_LAYOUT).matches
}

/** Follows the screen, so turning a phone or resizing a window switches the layout. */
export function usePhoneLayout(): boolean {
  return useSyncExternalStore(subscribe, isPhoneLayout, () => false)
}

function subscribe(onChange: () => void) {
  const list = window.matchMedia(PHONE_LAYOUT)
  list.addEventListener('change', onChange)
  return () => list.removeEventListener('change', onChange)
}
