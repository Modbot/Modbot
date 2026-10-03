// Where an event goes, and whether each place is set up (calendar design §14). Nothing here talks to
// the server or the page, so the Node test runner can load this file as it is: the same split
// `calendarGrid.ts` uses.
import type { CalendarEvent, CalendarEventInput, CalendarPlace, CalendarPlaceName, CalendarPlaceState } from './calendar.ts'

/**
 * VRChat: a managed group and a VRChat account. Discord: a server id and a connected bot. Google: a
 * key, a calendar, a good Check and Sending on (missing from an older server: taken as set up).
 */
export type CalendarReady = { vrChat: boolean; discord: boolean; google?: boolean }

/** Every place an event can go, as the form's chips name them, in their order. The feed is always on. */
export type CalendarDestination = 'vrchat' | 'discordEvent' | 'channelPost' | 'feed' | 'googleCalendar' | 'instance'

/**
 * A place switched by a plain tick on the event: every one but the feed, which is always on, and
 * Google Calendar, whose chip has rules of its own (`googleChip`).
 */
export type CalendarSwitchable = Exclude<CalendarDestination, 'feed' | 'googleCalendar'>

export const DESTINATIONS: readonly CalendarDestination[] = [
  'vrchat',
  'discordEvent',
  'channelPost',
  'feed',
  'googleCalendar',
  'instance',
]

export const DESTINATION_LABEL: Record<CalendarDestination, string> = {
  vrchat: 'VRChat calendar',
  discordEvent: 'Discord event',
  channelPost: 'Discord channel post',
  feed: 'Calendar feed',
  googleCalendar: 'Google Calendar',
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

/** A place that can say "Not set up": every chip but the feed. */
export type CalendarSetUpPlace = CalendarSwitchable | 'googleCalendar'

/** Where to set each place up: Modbot's VRChat login for VRChat, the Discord tab for Discord. */
export const SET_UP_LINK: Record<CalendarSetUpPlace, string> = {
  vrchat: '/settings#vrchat',
  instance: '/settings#vrchat',
  discordEvent: '/settings#discord',
  channelPost: '/settings#discord',
  googleCalendar: '/settings#google',
}

/**
 * Whether an event is for the group's members only, and so never goes to Google Calendar (Google
 * Calendar design decision 1): not visible to everyone, or shown on VRChat only to some of the
 * group's roles. The server's own rule (`CalendarGoogle.MembersOnly`), word for word.
 */
export function membersOnly(e: { visibility: string; vrChatRoleIds?: readonly string[] | null }): boolean {
  return e.visibility !== 'public' || (e.vrChatRoleIds?.length ?? 0) > 0
}

/** Whether an event is ticked for Google Calendar and may go there. */
export function wantsGoogle(e: {
  publishToGoogle?: boolean | null
  visibility: string
  vrChatRoleIds?: readonly string[] | null
}): boolean {
  return !!e.publishToGoogle && !membersOnly(e)
}

/** How the form's Google Calendar chip stands (Google Calendar design §3.8). */
export type GoogleChip = {
  /** Ticked, and the event may go: drawn on, and shown in Preview. */
  on: boolean
  /** The event is for members only (decision 1 A), so the chip cannot be ticked. */
  disabled: boolean
  /** What the chip says beside it: "Not set up", "Members only", or nothing. */
  note: 'notSetUp' | 'membersOnly' | null
}

/**
 * Whether the form's input is ticked for Google Calendar. A new event that nobody has clicked the
 * chip on starts ticked while Google Calendar is ready and the event is not for members only
 * (decision 6 A), so the tick follows "Visible to" until the chip is clicked; what this says is what
 * the form saves. An event that has been saved keeps its own tick.
 */
export function googleTicked(
  input: { publishToGoogle?: boolean | null; visibility: string },
  vrChatRoleIds: readonly string[] | null | undefined,
  ready: CalendarReady | null | undefined,
  isNew: boolean,
): boolean {
  if (input.publishToGoogle !== undefined && input.publishToGoogle !== null) return input.publishToGoogle
  return isNew && ready?.google === true && !membersOnly({ visibility: input.visibility, vrChatRoleIds })
}

/**
 * The form's Google Calendar chip. "Not set up" while Google Calendar is not set up or Sending is
 * off, ticked or not, since the chip is the way to it; otherwise "Members only" for an event only
 * members see, which cannot be ticked. A ticked event that becomes members-only keeps its tick and
 * is drawn off: made visible to everyone again, it goes. An older server that says nothing about
 * Google marks nothing.
 */
export function googleChip(
  input: { publishToGoogle?: boolean | null; visibility: string },
  vrChatRoleIds: readonly string[] | null | undefined,
  ready: CalendarReady | null | undefined,
  isNew: boolean,
): GoogleChip {
  const members = membersOnly({ visibility: input.visibility, vrChatRoleIds })
  const setUp = !ready || ready.google !== false

  return {
    on: googleTicked(input, vrChatRoleIds, ready, isNew) && !members,
    disabled: members,
    note: !setUp ? 'notSetUp' : members ? 'membersOnly' : null,
  }
}

/**
 * Whether the form shows "Visible to": while the VRChat calendar is on, or Google Calendar, which
 * also goes by it. A Google chip held back by "Members only" shows it too, since "Visible to" is
 * what lets it be ticked.
 */
export function showsVisibleTo(input: { publishToVRChat: boolean }, google: GoogleChip): boolean {
  return input.publishToVRChat || google.on || google.note === 'membersOnly'
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

/**
 * The ticked places that cannot work as things are set up now. Google Calendar is among them for an
 * event ticked for it that may go there, while Google is not set up or Sending is off.
 */
export function notSetUp(
  e: Pick<CalendarEventInput, 'publishToVRChat' | 'publishToDiscord' | 'postToChannel' | 'autoOpen'> & {
    publishToGoogle?: boolean | null
    visibility?: string
    vrChatRoleIds?: readonly string[] | null
  },
  ready: CalendarReady | null | undefined,
): CalendarSetUpPlace[] {
  const missing: CalendarSetUpPlace[] = wantedPlaces(e).filter((p) => !isSetUp(p, ready))
  if (
    ready &&
    ready.google === false &&
    wantsGoogle({ publishToGoogle: e.publishToGoogle, visibility: e.visibility ?? 'group', vrChatRoleIds: e.vrChatRoleIds })
  )
    missing.push('googleCalendar')
  return missing
}

/** One line under an event: a place, how it stands, and the server's row for it when there is one. */
export type PlaceLine = {
  place: CalendarPlaceName
  state: CalendarPlaceState
  /** Null for a place just ticked that Modbot has not started on yet: it is being sent all the same. */
  row: CalendarPlace | null
}

/** The order places are listed in under an event: the chips' order, then the cancel post. */
const LINE_ORDER: readonly CalendarPlaceName[] = ['vrchat', 'discordEvent', 'channelPost', 'googleCalendar', 'cancelPost']

/**
 * The places to list under an event (calendar design §17.3). Every place the server has a row for,
 * and, while the event is scheduled or open, every ticked place it has no row for yet, as being
 * sent: the loops make the row on their next pass, and until then the event would look as if
 * nothing were happening. A ticked place that is not set up is left out; it shows "Not set up".
 */
export function placeLines(
  event: Pick<CalendarEvent, 'state' | 'places' | 'publishToVRChat' | 'publishToDiscord' | 'postToChannel' | 'channelId' | 'autoOpen'> &
    Partial<Pick<CalendarEvent, 'publishToGoogle' | 'visibility' | 'vrChatRoleIds'>>,
  ready: CalendarReady | null | undefined,
): PlaceLine[] {
  const live = event.state === 'scheduled' || event.state === 'open'
  const pending = live || event.state === 'draft'
  const missing: string[] = pending ? notSetUp(event, ready) : []

  const wanted = new Set<CalendarPlaceName>()
  if (live && event.publishToVRChat) wanted.add('vrchat')
  if (live && event.publishToDiscord) wanted.add('discordEvent')
  if (live && event.postToChannel && event.channelId) wanted.add('channelPost')
  if (
    live &&
    ready?.google &&
    wantsGoogle({ publishToGoogle: event.publishToGoogle, visibility: event.visibility ?? 'group', vrChatRoleIds: event.vrChatRoleIds })
  )
    wanted.add('googleCalendar')

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
