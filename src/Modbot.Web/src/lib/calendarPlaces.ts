// Where an event goes, and whether each place is set up (calendar design §14). Nothing here talks to
// the server or the page, so the Node test runner can load this file as it is: the same split
// `calendarGrid.ts` uses.
import type { CalendarEventInput } from './calendar.ts'

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
