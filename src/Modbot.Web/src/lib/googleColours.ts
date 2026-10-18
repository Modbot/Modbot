// The colours an event can have on Google Calendar, and whether it shows as busy or free: Google's
// own fixed event palette (its ids "1" to "11", as the server's CalendarGoogleColours holds them).
// Nothing here talks to the server.

export type GoogleColour = { id: string; name: string; hex: string }

/** Google's event colours, in Google's order. */
export const GOOGLE_COLOURS: readonly GoogleColour[] = [
  { id: '1', name: 'Lavender', hex: '#a4bdfc' },
  { id: '2', name: 'Sage', hex: '#7ae7bf' },
  { id: '3', name: 'Grape', hex: '#dbadff' },
  { id: '4', name: 'Flamingo', hex: '#ff887c' },
  { id: '5', name: 'Banana', hex: '#fbd75b' },
  { id: '6', name: 'Tangerine', hex: '#ffb878' },
  { id: '7', name: 'Peacock', hex: '#46d6db' },
  { id: '8', name: 'Graphite', hex: '#e1e1e1' },
  { id: '9', name: 'Blueberry', hex: '#5484ed' },
  { id: '10', name: 'Basil', hex: '#51b749' },
  { id: '11', name: 'Tomato', hex: '#dc2127' },
]

/** Whether the event makes the calendar's owner busy while it runs. */
export type GoogleShowAs = 'busy' | 'free'

export const SHOW_AS_LABEL: Record<GoogleShowAs, string> = { busy: 'Busy', free: 'Free' }

/** The blue a calendar draws its events in when none is picked, drawn for "Calendar default". */
export const CALENDAR_DEFAULT_COLOUR = '#039be5'

/** The colour with Google's id, or null for none or an id Google does not have. */
export function googleColour(id: string | null | undefined): GoogleColour | null {
  return GOOGLE_COLOURS.find((c) => c.id === id) ?? null
}

/** The colour to draw for an event: the picked one, else the calendar's own. */
export function googleColourHex(id: string | null | undefined): string {
  return googleColour(id)?.hex ?? CALENDAR_DEFAULT_COLOUR
}

/** The name of the colour, "Calendar default" for none. */
export function googleColourName(id: string | null | undefined): string {
  return googleColour(id)?.name ?? 'Calendar default'
}
