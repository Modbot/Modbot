import type { CalendarViewName } from './calendarGrid.ts'

/**
 * Which view the Calendar opens on (UX review 2026-09-27, idea 15).
 *
 * A view picked with the switch or its key is remembered in the browser, so the page opens the way
 * it was left. Until one is picked, a phone opens on Schedule -- a list of what is coming -- because
 * a Day grid there is mostly empty hours; a desk opens on Week. Opening one day from Month is a
 * look, not a pick, and is not remembered.
 */

const KEY = 'modbot.calendar.view'

const NAMES: readonly CalendarViewName[] = ['day', 'week', 'month', 'schedule']

export function recallView(): CalendarViewName | null {
  try {
    const raw = localStorage.getItem(KEY)
    return NAMES.find((name) => name === raw) ?? null
  } catch {
    return null
  }
}

export function rememberView(view: CalendarViewName): void {
  try {
    localStorage.setItem(KEY, view)
  } catch {
    // A blocked store forgets; the page still opens on its default.
  }
}

/** The view to open on: the one last picked, or else Schedule on a phone and Week at a desk. */
export function firstView(phone: boolean): CalendarViewName {
  return recallView() ?? (phone ? 'schedule' : 'week')
}
