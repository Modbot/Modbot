import type { AuditRequest } from './api.ts'
import type { LiveEvent } from './liveStream.ts'

/**
 * Which live events each screen redraws for (live updates design §6.1).
 *
 * Pure, so a test can check the rules without a browser. A screen asks one question -- "does
 * this event change what I am showing?" -- and reads again when the answer is yes. The event's
 * payload is never applied by hand: the server has already written the change, so reading it back
 * is the one way the screen and the server cannot disagree.
 */

const startsWithAny = (type: string, prefixes: readonly string[]) => prefixes.some((p) => type.startsWith(p))

/** Somebody arrived, left, was already here, or a watch ended: what the roster and the Live page show. */
export const PRESENCE_TYPES = ['vrchat.instance.'] as const

/** A room opened, closed or changed. */
export const ROOM_TYPES = ['vrchat.group.instance.'] as const

/** The group's membership: joins, leaves, bans, kicks, roles, and profiles (trust rank among them). */
export const MEMBER_TYPES = ['vrchat.group.member.', 'vrchat.group.members.', 'vrchat.group.role.', 'vrchat.user.'] as const

/** The Discord server's members, roles and account links. */
export const DISCORD_MEMBER_TYPES = ['discord.member.', 'discord.members.', 'discord.role.', 'discord.link.'] as const

/** The ban list. */
export const BAN_TYPES = ['vrchat.group.member.ban', 'vrchat.group.member.unban', 'vrchat.group.bans.'] as const

/** Case files and the reports in them. */
export const CASE_TYPES = ['modbot.report.', 'modbot.evidence.'] as const

/** Flags from moderation rules. */
export const FLAG_TYPES = ['modbot.ai-moderation.'] as const

/** Reviews of a moderator's pattern. */
export const REVIEW_TYPES = ['modbot.review.'] as const

/** Planned events and VRChat's calendar. */
export const CALENDAR_TYPES = ['modbot.calendar.', 'vrchat.group.calendar-event.'] as const

export const changesLive = (e: LiveEvent) => startsWithAny(e.type, PRESENCE_TYPES) || startsWithAny(e.type, ROOM_TYPES)
export const changesMembers = (e: LiveEvent) => startsWithAny(e.type, MEMBER_TYPES)
export const changesDiscordMembers = (e: LiveEvent) => startsWithAny(e.type, DISCORD_MEMBER_TYPES)
export const changesBans = (e: LiveEvent) => startsWithAny(e.type, BAN_TYPES)
export const changesCases = (e: LiveEvent) => startsWithAny(e.type, CASE_TYPES)
export const changesFlags = (e: LiveEvent) => startsWithAny(e.type, FLAG_TYPES)
export const changesReviews = (e: LiveEvent) => startsWithAny(e.type, REVIEW_TYPES)
export const changesCalendar = (e: LiveEvent) => startsWithAny(e.type, CALENDAR_TYPES)
export const isAlert = (e: LiveEvent) => e.kind === 'alert'

/** Anything recorded about one person: as the subject, or as the one who did it. */
export function concernsPerson(event: LiveEvent, id: string, platform: 'VRChat' | 'Discord' = 'VRChat'): boolean {
  if (event.subject.id === id && event.subject.platform === platform) return true
  return event.actor !== null && event.actor.id === id && event.actor.platform === platform
}

/** Anything that happened in one world. */
export function concernsWorld(event: LiveEvent, worldId: string): boolean {
  return event.worldId === worldId
}

/** Anything that happened in one room, by its VRChat instance number. */
export function concernsInstance(event: LiveEvent, vrChatInstanceId: string | null | undefined): boolean {
  return !!vrChatInstanceId && event.instanceId === vrChatInstanceId
}

/**
 * Whether an entry would appear in the audit log as it is filtered now, so a new row is counted
 * only when it belongs on the list in front of the moderator.
 *
 * The same rules the server applies, as far as an event can tell. `q` searches the ids and the
 * payload's text, which is what the server searches; a fact is counted when any of it contains
 * the words.
 */
export function auditMatches(event: LiveEvent, query: Omit<AuditRequest, 'limit' | 'before'>): boolean {
  if (query.source?.length && !query.source.includes(event.source)) return false
  if (query.type?.length && !query.type.includes(event.type)) return false
  if (query.category && query.category.toLowerCase() !== event.category.toLowerCase()) return false

  if (query.subject && event.subject.id !== query.subject) return false
  if (query.subjectPlatform && event.subject.platform !== query.subjectPlatform) return false

  if (query.actor) {
    const actor = event.actor
    if (!actor) return false
    const wanted = query.actor.toLowerCase()
    if (actor.id.toLowerCase() !== wanted && (actor.name ?? '').toLowerCase() !== wanted) return false
  }

  if (query.actorPlatform && event.actor?.platform !== query.actorPlatform) return false
  if (query.world && event.worldId !== query.world) return false
  if (query.instance && event.instanceId !== query.instance) return false

  if (query.precision === 'Exact' && event.occurredBefore !== null) return false
  if (query.precision === 'Window' && event.occurredBefore === null) return false

  if (query.hasActor === true && !event.actor) return false
  if (query.hasActor === false && event.actor) return false

  if (query.from && event.at < query.from) return false
  if (query.to && event.at > query.to) return false

  if (query.q) {
    const words = query.q.toLowerCase()
    const text = [event.subject.id, event.actor?.id ?? '', event.actor?.name ?? '', JSON.stringify(event.data ?? '')]
      .join('\n')
      .toLowerCase()
    if (!text.includes(words)) return false
  }

  return true
}
