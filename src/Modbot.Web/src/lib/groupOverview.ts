// Relative, with the extension, so the Node test runner can load this file as it is (see nav.ts).
// Every import is either a type, which is stripped, or nav.ts, which loads on its own.
import type { CurrentUser } from './api.ts'
import type { CalendarEvent } from './calendar.ts'
import { mayOpen, type PageId } from './nav.ts'

/**
 * The pieces of the VRChat analytics page's top half that are worth a test: which tabs a person
 * sees, the group's code, a language's name, a link's label and the next event.
 */

/**
 * The row under the group's header, named the way VRChat's own group page names its tabs, each
 * leading to the Modbot page that shows that part of the group. Overview is the page itself.
 */
export const GROUP_TABS: readonly { id: PageId; label: string }[] = [
  { id: 'analytics-group', label: 'Overview' },
  { id: 'calendar', label: 'Events' },
  { id: 'analytics-instances', label: 'Instances' },
  { id: 'members', label: 'Members' },
  { id: 'requests', label: 'Requests' },
  { id: 'bans', label: 'Bans' },
]

/** The tabs this person may open, in order: a page the sidebar hides is not offered here either. */
export function groupTabs(me: CurrentUser): { id: PageId; label: string }[] {
  return GROUP_TABS.filter((tab) => mayOpen(me, tab.id))
}

/** `TESTIN.4698`, the way VRChat writes a group's code; the code alone without the digits. */
export function groupCode(shortCode: string | null, discriminator: string | null): string | null {
  const code = shortCode?.trim()
  if (!code) return null

  const digits = discriminator?.trim()
  return digits ? `${code}.${digits}` : code
}

/**
 * The few codes VRChat offers that the browser's own list of language names may not know. VRChat
 * lists sign languages beside spoken ones, and a code such as `bfi` shown as it is means nothing.
 */
const LANGUAGE_NAMES: Record<string, string> = {
  ase: 'American Sign Language',
  bfi: 'British Sign Language',
  dse: 'Dutch Sign Language',
  fsl: 'French Sign Language',
  jsl: 'Japanese Sign Language',
  kvk: 'Korean Sign Language',
}

/**
 * A language's name from VRChat's code (`eng` → English). The browser knows most codes; the few
 * it may not are above, and one neither knows is written as the code in capitals.
 */
export function languageName(code: string, locale?: string): string {
  const key = code.trim().toLowerCase()
  if (!key) return code

  if (LANGUAGE_NAMES[key]) return LANGUAGE_NAMES[key]

  try {
    const name = new Intl.DisplayNames(locale ? [locale] : undefined, { type: 'language', fallback: 'none' }).of(key)
    if (name && name.toLowerCase() !== key) return name
  } catch {
    // Not a code the browser will read at all.
  }

  return key.toUpperCase()
}

/** A link as short readable text: `discord.gg/example`, without the `https://` or a lone `/`. */
export function linkLabel(url: string): string {
  try {
    const parsed = new URL(url)
    const host = parsed.hostname.replace(/^www\./, '')
    const path = parsed.pathname === '/' ? '' : parsed.pathname.replace(/\/$/, '')
    return `${host}${path}${parsed.search}`
  } catch {
    return url
  }
}

/** Whether a link may go in an `href`. The server keeps only these; this is the page's own check. */
export function isWebLink(url: string): boolean {
  try {
    const { protocol } = new URL(url)
    return protocol === 'https:' || protocol === 'http:'
  } catch {
    return false
  }
}

export type NextEvent = { event: CalendarEvent; startsAt: string; endsAt: string }

/**
 * The event that comes next: the earliest occurrence that has not ended yet, so one already under
 * way counts until it finishes. Drafts, cancelled and finished events are left out.
 */
export function nextEvent(events: readonly CalendarEvent[], now: string): NextEvent | null {
  const at = new Date(now).getTime()
  let best: NextEvent | null = null

  for (const event of events) {
    if (event.state === 'draft' || event.state === 'cancelled' || event.state === 'finished') continue

    for (const o of event.occurrences) {
      if (new Date(o.endsAt).getTime() <= at) continue
      if (!best || new Date(o.startsAt).getTime() < new Date(best.startsAt).getTime()) {
        best = { event, startsAt: o.startsAt, endsAt: o.endsAt }
      }
    }
  }

  return best
}
