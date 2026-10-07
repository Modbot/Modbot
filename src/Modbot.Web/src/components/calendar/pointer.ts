import type { PointerEvent as ReactPointerEvent } from 'react'

/** How far a mouse moves before a press is a drag rather than a click. */
const MOUSE_SLOP_PX = 4

/** How far a finger may wander while it is held, before the hold is taken as a scroll instead. */
const TOUCH_SLOP_PX = 8

/** How long a finger is held before it picks something up. */
export const LONG_PRESS_MS = 400

export type PointerPoint = { x: number; y: number }

export type PointerSession = {
  /** The press became a drag: past the slop with a mouse, or held long enough with a finger. */
  onStart?: (at: PointerPoint) => void
  /** Each move while dragging. */
  onMove?: (at: PointerPoint) => void
  /** Let go after a drag. */
  onEnd?: (at: PointerPoint) => void
  /** Let go without dragging: a click, or a tap. */
  onTap?: (at: PointerPoint) => void
  /** The browser took the pointer away (a scroll, a second finger): nothing is saved. */
  onCancel?: () => void
  /** How long a finger is held before it picks something up, when this press wants a different wait. */
  holdMs?: number
}

/**
 * One press, from pointer down to let go, told apart as a tap or a drag.
 *
 * A mouse drags as soon as it moves a few pixels. A finger has to be **held** first, the way
 * Google Calendar's app works: a finger that moves straight away is scrolling the page, and
 * taking that for a drag would make the calendar impossible to scroll on a phone. Once a hold has
 * picked something up, the page's own scrolling is held off until the finger lets go.
 *
 * Moves and the let-go are listened for on the window, so a drag that leaves the element it
 * started on (into the next day, off the bottom of the grid) keeps going.
 */
export function beginPress(event: ReactPointerEvent, session: PointerSession): void {
  if (event.pointerType === 'mouse' && event.button !== 0) return

  const touch = event.pointerType === 'touch'
  const id = event.pointerId
  const origin = { x: event.clientX, y: event.clientY }
  let dragging = false
  let done = false
  let holdTimer: ReturnType<typeof setTimeout> | null = null
  let last = origin

  const point = (e: PointerEvent): PointerPoint => ({ x: e.clientX, y: e.clientY })

  // While a finger is dragging, a touchmove that is not cancelled scrolls the page under it.
  const holdScroll = (e: TouchEvent) => {
    if (dragging) e.preventDefault()
  }

  const finish = () => {
    done = true
    if (holdTimer) clearTimeout(holdTimer)
    window.removeEventListener('pointermove', move)
    window.removeEventListener('pointerup', up)
    window.removeEventListener('pointercancel', cancel)
    window.removeEventListener('touchmove', holdScroll)
    window.removeEventListener('contextmenu', noMenu, true)
  }

  const start = () => {
    if (done || dragging) return
    dragging = true
    session.onStart?.(last)
    session.onMove?.(last)
  }

  const move = (e: PointerEvent) => {
    if (e.pointerId !== id) return
    last = point(e)

    if (!dragging) {
      const distance = Math.hypot(last.x - origin.x, last.y - origin.y)
      if (touch) {
        // Moved before the hold: a scroll, and the browser has it.
        if (distance > TOUCH_SLOP_PX) {
          finish()
          session.onCancel?.()
        }
        return
      }
      if (distance < MOUSE_SLOP_PX) return
      start()
      return
    }

    session.onMove?.(last)
  }

  const up = (e: PointerEvent) => {
    if (e.pointerId !== id) return
    finish()
    if (dragging) session.onEnd?.(point(e))
    else session.onTap?.(point(e))
  }

  const cancel = (e: PointerEvent) => {
    if (e.pointerId !== id) return
    finish()
    session.onCancel?.()
  }

  // A long press on a phone opens the browser's own menu; here it picks the event up instead.
  const noMenu = (e: Event) => e.preventDefault()

  window.addEventListener('pointermove', move)
  window.addEventListener('pointerup', up)
  window.addEventListener('pointercancel', cancel)

  if (touch) {
    window.addEventListener('touchmove', holdScroll, { passive: false })
    window.addEventListener('contextmenu', noMenu, true)
    holdTimer = setTimeout(() => {
      holdTimer = null
      start()
      navigator.vibrate?.(10)
    }, session.holdMs ?? LONG_PRESS_MS)
  }
}

/** The element under a point that carries `data-day-index`, and that index. */
export function dayIndexAt(at: PointerPoint): number | null {
  const hit = document.elementFromPoint(at.x, at.y)?.closest<HTMLElement>('[data-day-index]')
  const index = hit?.dataset.dayIndex
  return index === undefined ? null : Number(index)
}
