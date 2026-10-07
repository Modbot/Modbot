import type { PointerEvent as ReactPointerEvent } from 'react'
import type { PointerPoint } from '@/components/calendar/pointer'

/**
 * A finger painting at once: from the moment it touches to the moment it lets go, every move is
 * reported, and nothing under it scrolls. For Paint on a touch screen, where `beginPress` would make
 * the finger hold first.
 *
 * The grid is `touch-action: none` while this is on, so the browser never takes the finger for a
 * scroll. The pointer is captured as well, and moves are listened for on the window, so a stroke
 * that leaves the first cell, or the grid, keeps going. The pointer events themselves stay on the
 * cell the finger went down on, which is why the caller looks up the cell under each point.
 */
export function beginPaintStroke(
  event: ReactPointerEvent,
  session: {
    /** Each move of the finger, in page coordinates. */
    onMove: (at: PointerPoint) => void
    /** Let go, or the browser took the pointer away. */
    onEnd?: () => void
  },
): void {
  const id = event.pointerId

  try {
    event.currentTarget.setPointerCapture(id)
  } catch {
    // Without capture the window still hears the finger.
  }

  const finish = () => {
    window.removeEventListener('pointermove', move)
    window.removeEventListener('pointerup', end)
    window.removeEventListener('pointercancel', end)
    window.removeEventListener('touchmove', holdScroll)
    window.removeEventListener('contextmenu', noMenu, true)
    session.onEnd?.()
  }

  const move = (e: PointerEvent) => {
    if (e.pointerId !== id) return
    session.onMove({ x: e.clientX, y: e.clientY })
  }

  const end = (e: PointerEvent) => {
    if (e.pointerId === id) finish()
  }

  // A browser that ignores touch-action would still scroll under a touchmove that is not cancelled.
  const holdScroll = (e: TouchEvent) => e.preventDefault()

  // A finger held still on a cell opens the browser's own menu otherwise.
  const noMenu = (e: Event) => e.preventDefault()

  window.addEventListener('pointermove', move)
  window.addEventListener('pointerup', end)
  window.addEventListener('pointercancel', end)
  window.addEventListener('touchmove', holdScroll, { passive: false })
  window.addEventListener('contextmenu', noMenu, true)
}
