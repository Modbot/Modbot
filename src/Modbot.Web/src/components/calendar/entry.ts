import type { CalendarEvent } from '@/lib/calendar'
import { eventTone, type EventTone } from '@/lib/calendarGrid'

/** One occurrence of one event: what every view draws and every drag moves. */
export type Entry = { key: string; event: CalendarEvent; start: Date; end: Date }

/** A new time for an occurrence, and whether its start moved or only its end. */
export type Change = { start: Date; end: Date; kind: 'move' | 'resize' }

/** Where something on screen is, for a popover to point at. */
export type Spot = { left: number; top: number; width: number; height: number }

export function spotOf(element: Element): Spot {
  const r = element.getBoundingClientRect()
  return { left: r.left, top: r.top, width: r.width, height: r.height }
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

/** Whether this person may drag, resize or edit this event. */
export function mayChange(canManage: boolean, event: CalendarEvent): boolean {
  return canManage && event.state !== 'cancelled'
}
