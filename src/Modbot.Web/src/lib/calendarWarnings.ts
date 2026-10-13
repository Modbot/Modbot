// Nothing here talks to the server or the page, so the Node test runner can load this file as it is.

/** An event the server found with the same title, starting at nearly the same time. */
export type CalendarSameEvent = {
  id: string
  title: string
  startsAt: string
  /** It was made on VRChat's group calendar, not in Modbot. */
  onVRChat: boolean
}

export type CalendarSameEvents = {
  matches: CalendarSameEvent[]
}

/** What the warnings look at: the form as it stands, less the dates. */
export type SaveWarningInput = {
  title: string
  publishToVRChat: boolean
  languages: string[]
  tags: string[]
  /** The title the template gave this event, when it was made from one. */
  templateTitle?: string
}

/** A start as a person reads it: `Fri 14 Nov, 20:00`, in the event's own zone. */
export function startWords(startsAt: string, timeZone: string): string {
  const at = new Date(startsAt)
  if (Number.isNaN(at.getTime())) return startsAt

  try {
    return new Intl.DateTimeFormat('en-GB', {
      weekday: 'short',
      day: 'numeric',
      month: 'short',
      hour: '2-digit',
      minute: '2-digit',
      hour12: false,
      timeZone,
    }).format(at)
  } catch {
    return startsAt
  }
}

/**
 * The warnings to show before a save, one short line each. None of them stops the save: the person
 * reads them and saves anyway. An empty list says nothing is wrong.
 */
export function saveWarnings(input: SaveWarningInput, same: CalendarSameEvent[], timeZone: string): string[] {
  const lines: string[] = []

  if (input.publishToVRChat) {
    if (input.tags.length === 0) lines.push('No VRChat tags.')
    if (input.languages.length === 0) lines.push('No VRChat language.')
  }

  const title = input.title.trim()
  if (input.templateTitle !== undefined && title !== input.templateTitle.trim()) {
    lines.push(`The title differs from the template's: "${input.templateTitle.trim()}".`)
  }

  for (const match of same) {
    lines.push(
      `"${match.title}" already starts ${startWords(match.startsAt, timeZone)}${match.onVRChat ? ' on VRChat' : ''}.`,
    )
  }

  return lines
}
