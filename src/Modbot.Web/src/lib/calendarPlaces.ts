// Where an event goes, and whether each place is set up (calendar design §14). Nothing here talks to
// the server or the page, so the Node test runner can load this file as it is: the same split
// `calendarGrid.ts` uses.
import type { CalendarEvent, CalendarEventInput, CalendarPlace, CalendarPlaceName, CalendarPlaceState } from './calendar.ts'

/** VRChat: a managed group and a VRChat account. Discord: a server id and a connected bot. */
export type CalendarReady = { vrChat: boolean; discord: boolean }

/** Every place an event can go, as the form's chips name them, in their order. The feed is always on. */
export type CalendarDestination = 'vrchat' | 'discordEvent' | 'channelPost' | 'feed' | 'instance'

/** A place that can be switched, which is every one but the feed. */
export type CalendarSwitchable = Exclude<CalendarDestination, 'feed'>

export const DESTINATIONS: readonly CalendarDestination[] = ['vrchat', 'discordEvent', 'channelPost', 'feed', 'instance']

export const DESTINATION_LABEL: Record<CalendarDestination, string> = {
  vrchat: 'VRChat calendar',
  discordEvent: 'Discord event',
  channelPost: 'Discord channel post',
  feed: 'Calendar feed',
  instance: 'Open the instance',
}

/** The switch on the event behind each chip. */
export const DESTINATION_SWITCH: Record<
  CalendarSwitchable,
  keyof Pick<CalendarEventInput, 'publishToVRChat' | 'publishToDiscord' | 'postToChannel' | 'autoOpen'>
> = {
  vrchat: 'publishToVRChat',
  discordEvent: 'publishToDiscord',
  channelPost: 'postToChannel',
  instance: 'autoOpen',
}

/** Where to set each place up: Modbot's VRChat login for VRChat, the Discord tab for Discord. */
export const SET_UP_LINK: Record<CalendarSwitchable, string> = {
  vrchat: '/settings#vrchat',
  instance: '/settings#vrchat',
  discordEvent: '/settings#discord',
  channelPost: '/settings#discord',
}

/** The places an event, or the form's input, is ticked for, in the chips' order. */
export function wantedPlaces(
  e: Pick<CalendarEventInput, 'publishToVRChat' | 'publishToDiscord' | 'postToChannel' | 'autoOpen'>,
): CalendarSwitchable[] {
  return (['vrchat', 'discordEvent', 'channelPost', 'instance'] as const).filter((p) => e[DESTINATION_SWITCH[p]])
}

/**
 * Whether a place can work as things are set up now: the server's `CalendarReadiness` sends the
 * two answers this reads. An older server sends none, and then nothing is marked.
 */
export function isSetUp(place: CalendarSwitchable, ready: CalendarReady | null | undefined): boolean {
  if (!ready) return true
  return place === 'vrchat' || place === 'instance' ? ready.vrChat : ready.discord
}

/** The ticked places that cannot work as things are set up now. */
export function notSetUp(
  e: Pick<CalendarEventInput, 'publishToVRChat' | 'publishToDiscord' | 'postToChannel' | 'autoOpen'>,
  ready: CalendarReady | null | undefined,
): CalendarSwitchable[] {
  return wantedPlaces(e).filter((p) => !isSetUp(p, ready))
}

/** One line under an event: a place, how it stands, and the server's row for it when there is one. */
export type PlaceLine = {
  place: CalendarPlaceName
  state: CalendarPlaceState
  /** Null for a place just ticked that Modbot has not started on yet: it is being sent all the same. */
  row: CalendarPlace | null
}

/** The order places are listed in under an event: the chips' order, then the cancel post. */
const LINE_ORDER: readonly CalendarPlaceName[] = ['vrchat', 'discordEvent', 'channelPost', 'cancelPost']

/**
 * The places to list under an event (calendar design §17.3). Every place the server has a row for,
 * and, while the event is scheduled or open, every ticked place it has no row for yet, as being
 * sent: the loops make the row on their next pass, and until then the event would look as if
 * nothing were happening. A ticked place that is not set up is left out; it shows "Not set up".
 */
export function placeLines(
  event: Pick<CalendarEvent, 'state' | 'places' | 'publishToVRChat' | 'publishToDiscord' | 'postToChannel' | 'channelId' | 'autoOpen'>,
  ready: CalendarReady | null | undefined,
): PlaceLine[] {
  const live = event.state === 'scheduled' || event.state === 'open'
  const pending = live || event.state === 'draft'
  const missing: string[] = pending ? notSetUp(event, ready) : []

  const wanted = new Set<CalendarPlaceName>()
  if (live && event.publishToVRChat) wanted.add('vrchat')
  if (live && event.publishToDiscord) wanted.add('discordEvent')
  if (live && event.postToChannel && event.channelId) wanted.add('channelPost')

  const lines: PlaceLine[] = []

  for (const place of LINE_ORDER) {
    if (missing.includes(place)) continue

    const row = event.places.find((p) => p.place === place) ?? null
    if (row) lines.push({ place, state: row.state, row })
    else if (wanted.has(place)) lines.push({ place, state: 'waiting', row: null })
  }

  return lines
}

/** Whether anything under the event is still being sent, so the page should look again soon. */
export function anySending(lines: readonly PlaceLine[]): boolean {
  return lines.some((l) => l.state === 'waiting')
}

/** The form's own missing piece: a channel post with no channel picked. */
export function missingChannel(e: Pick<CalendarEventInput, 'postToChannel' | 'channelId'>): boolean {
  return e.postToChannel && !e.channelId
}

/** Discord's limits for an event's name and description, which are Modbot's too (calendar design §2). */
export const TITLE_LIMIT = 100
export const DESCRIPTION_LIMIT = 1000

/** "12 / 100", and whether it is over: what the form shows beside the title and the description. */
export function counted(text: string, limit: number): { label: string; over: boolean } {
  // Counted the way the server counts: after trimming, in UTF-16 units (C#'s string.Length).
  const length = text.trim().length
  return { label: `${length} / ${limit}`, over: length > limit }
}
