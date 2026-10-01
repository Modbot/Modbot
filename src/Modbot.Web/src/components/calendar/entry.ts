import type { CalendarEvent, CalendarOccurrence } from '@/lib/calendar'
import { eventTone, type EventTone } from '@/lib/calendarGrid'

/**
 * One occurrence of one event: what every view draws and every drag moves. `date` is the planned
 * start a change to this one date names it by (calendar design §2.2); `title` is the date's own or
 * the event's; a date cancelled on its own is drawn struck through and moves nowhere.
 */
export type Entry = {
  key: string
  event: CalendarEvent
  start: Date
  end: Date
  date: string
  title: string
  cancelled: boolean
}

/** A new time for an occurrence, and whether its start moved or only its end. */
export type Change = { start: Date; end: Date; kind: 'move' | 'resize' }

/** Where something on screen is, for a popover to point at. */
export type Spot = { left: number; top: number; width: number; height: number }

export function spotOf(element: Element): Spot {
  const r = element.getBoundingClientRect()
  return { left: r.left, top: r.top, width: r.width, height: r.height }
}

/** The event's date that starts at `start`, and whether it was cancelled on its own; null when it has none then. */
export function dateAt(
  event: CalendarEvent,
  start: Date,
): { occurrence: CalendarOccurrence; cancelled: boolean } | null {
  const at = start.getTime()
  const live = event.occurrences.find((o) => new Date(o.startsAt).getTime() === at)
  if (live) return { occurrence: live, cancelled: false }
  const gone = (event.cancelledDates ?? []).find((o) => new Date(o.startsAt).getTime() === at)
  return gone ? { occurrence: gone, cancelled: true } : null
}

const TONE_CLASS: Record<EventTone, string> = {
  scheduled: 'border-accent-foreground/30 border-l-primary bg-accent text-accent-foreground',
  open: 'border-ok/40 border-l-ok bg-ok/15 text-foreground',
  draft: 'border-dashed border-input border-l-input bg-card text-muted-foreground',
  finished: 'border-border border-l-border bg-muted text-muted-foreground',
  failed: 'border-destructive/40 border-l-destructive bg-destructive/10 text-foreground',
}

/** The colours of an event's block or chip, from its publish state (calendarGrid `eventTone`). */
export function toneClass(event: CalendarEvent): string {
  return TONE_CLASS[eventTone(event)]
}

/** One date's colours: the event's, faded and struck through when the date was cancelled on its own. */
export function entryClass(entry: Entry): string {
  return entry.cancelled ? `${TONE_CLASS.finished} line-through opacity-70` : toneClass(entry.event)
}

/** Whether this person may drag, resize or edit this event. */
export function mayChange(canManage: boolean, event: CalendarEvent): boolean {
  return canManage && event.state !== 'cancelled'
}

/** Whether this person may drag or resize this one date: never one cancelled on its own. */
export function mayMove(canManage: boolean, entry: Entry): boolean {
  return mayChange(canManage, entry.event) && !entry.cancelled
}
