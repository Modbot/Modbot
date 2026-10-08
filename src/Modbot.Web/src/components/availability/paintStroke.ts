import type { PointerEvent as ReactPointerEvent } from 'react'
import type { PointerPoint } from '@/components/calendar/pointer'

/**
 * A finger painting at once: from the moment it touches to the moment it lets go, every move is
 * reported, and nothing under it scrolls. For a finger on a cell, where `beginPress` would make the
 * finger hold first.
 *
 * The cells are `touch-action: none`, so the browser never takes a finger that went down on one for
 * a scroll; the labels around them are not, and still scroll the page. The pointer is captured as well, and moves are listened for on the window, so a stroke
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
