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
 * The row under the group's header, named and ordered the way vrchat.com names a group's tabs:
 * Overview · Posts · Events · Instances · Gallery · Members · Invites · Settings · Banned Users.
 *
 * Overview, Posts, Instances, Gallery, Invites and Settings are the VRChat page itself, each at an
 * address of its own under it; Events, Members and Banned Users lead to the Modbot pages that show
 * that part of the group.
 */
export const GROUP_TABS: readonly { id: PageId; label: string }[] = [
  { id: 'analytics-group', label: 'Overview' },
  { id: 'group-posts', label: 'Posts' },
  { id: 'calendar', label: 'Events' },
  { id: 'analytics-instances', label: 'Instances' },
  { id: 'group-gallery', label: 'Gallery' },
  { id: 'members', label: 'Members' },
  { id: 'group-invites', label: 'Invites' },
  { id: 'group-settings', label: 'Settings' },
  { id: 'bans', label: 'Banned Users' },
]

/**
 * The tabs this person may open, in order: a page the sidebar hides is not offered here either.
 * Settings opens at General, or at Roles for somebody who may manage roles but not the profile.
 */
export function groupTabs(me: CurrentUser): { id: PageId; label: string }[] {
  return GROUP_TABS.flatMap((tab) => {
    if (mayOpen(me, tab.id)) return [tab]
    if (tab.id === 'group-settings' && mayOpen(me, 'group-roles')) return [{ id: 'group-roles' as PageId, label: tab.label }]
    return []
  })
}

/** The Settings tab's own id for this person: the one `groupTabs` gives them. */
export function settingsTab(me: CurrentUser): PageId {
  return mayOpen(me, 'group-settings') ? 'group-settings' : 'group-roles'
}

/**
 * The tabs that lead to a Modbot page of its own rather than to the VRChat page (Events, Members,
 * Banned Users, and Settings' Logs). Opened from the tab row, their address carries `from=group`, and
 * that page then draws the group's header above itself so the person has not left the group's page.
 * Opened from the sidebar, the same page looks as it always has.
 */
const OWN_PAGES: readonly PageId[] = ['calendar', 'members', 'bans', 'audit']

/** The address a tab of the row leads to: `path` itself, or `path?from=group` for a page of its own. */
export function groupTabHref(id: PageId, path: string): string {
  return OWN_PAGES.includes(id) ? `${path}${path.includes('?') ? '&' : '?'}from=group` : path
}

/**
 * The tab to mark when `page` was opened from the group's tab row, or null when it was not, or is
 * not one of the pages the row leads out to. Members opens as People narrowed to members, so People
 * reached that way is the Members tab; Logs is inside Settings.
 */
export function groupTabFrom(page: PageId, search: URLSearchParams, me: CurrentUser): PageId | null {
  if (search.get('from') !== 'group') return null

  if (page === 'calendar' || page === 'bans') return page
  if (page === 'people') return 'members'
  if (page === 'audit') return settingsTab(me)
  return null
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
