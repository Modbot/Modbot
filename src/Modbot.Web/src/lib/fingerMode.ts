import { useSyncExternalStore } from 'react'

/**
 * What a finger does on the Availability week grid: paint the moment it moves (and the grid does
 * not scroll), or scroll the page as usual and paint only after a short hold.
 *
 * On a touch screen the choice is remembered in the browser. Until one is made it is Paint, because
 * a grid that scrolls under the finger looks like a grid that cannot be painted.
 */
export type FingerMode = 'paint' | 'scroll'

const KEY = 'modbot.availability.finger'

export function recallFingerMode(): FingerMode | null {
  try {
    const raw = localStorage.getItem(KEY)
    return raw === 'paint' || raw === 'scroll' ? raw : null
  } catch {
    return null
  }
}

export function rememberFingerMode(mode: FingerMode): void {
  try {
    localStorage.setItem(KEY, mode)
  } catch {
    // A blocked store forgets; the page still opens on its default.
  }
}

/** The mode to open on: the one last picked, or else Paint. */
export function firstFingerMode(): FingerMode {
  return recallFingerMode() ?? 'paint'
}

/** A screen whose main pointer is a finger. A touch laptop reports its mouse and does not match. */
export const TOUCH_INPUT = '(pointer: coarse)'

export function isTouchInput(): boolean {
  return typeof window !== 'undefined' && window.matchMedia(TOUCH_INPUT).matches
}

/** Follows the screen, so plugging in a mouse or docking a tablet switches it. */
export function useTouchInput(): boolean {
  return useSyncExternalStore(subscribe, isTouchInput, () => false)
}

function subscribe(onChange: () => void) {
  const list = window.matchMedia(TOUCH_INPUT)
  list.addEventListener('change', onChange)
  return () => list.removeEventListener('change', onChange)
}
