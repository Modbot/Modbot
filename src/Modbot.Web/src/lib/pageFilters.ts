import type { AuditRequest, DiscordMemberQuery, PeopleQuery } from './api.ts'
import { chipFor, dateRange, writeChips, yesNo, type FilterChip } from './filters.ts'

/**
 * What each page's chips ask the server. Pure, so a test can check that a chip becomes the
 * query parameter it should, without a browser.
 */

/**
 * The audit log's default: VRChat, Discord, Companion App and Import on, Sync off, so a sweep's noticed
 * changes do not crowd the exact entries. Import is on because old data is what somebody uploaded
 * on purpose (import design §5).
 */
export const AUDIT_DEFAULTS: FilterChip[] = [
  { property: 'source', operator: 'is', values: ['AuditLog', 'Discord', 'Companion', 'Import'] },
]

export function auditQueryFrom(chips: FilterChip[]): Omit<AuditRequest, 'limit' | 'before'> {
  const source = chipFor(chips, 'source')
  const type = chipFor(chips, 'type')
  const category = chipFor(chips, 'category')
  const actor = chipFor(chips, 'actor')
  const subject = chipFor(chips, 'subject')
  const world = chipFor(chips, 'world')
  const instance = chipFor(chips, 'instance')
  const precision = chipFor(chips, 'precision')
  const text = chipFor(chips, 'text')
  const when = dateRange(chips, 'when')

  // "Is not" on a fixed list is the rest of the list, which the server takes as a plain list.
  const SOURCES = ['AuditLog', 'SyncDiff', 'Companion', 'Discord', 'Manual', 'Modbot', 'Import']

  return {
    source:
      source?.operator === 'is-not'
        ? SOURCES.filter((s) => !source.values.includes(s))
        : source?.values.length
          ? source.values
          : undefined,
    // The type list comes from the server, so "is not" goes as its own parameter.
    type: type?.operator !== 'is-not' && type?.values.length ? type.values : undefined,
    notType: type?.operator === 'is-not' && type.values.length ? type.values : undefined,
    category: category?.values[0] as AuditRequest['category'],
    actor: actor?.values[0] || undefined,
    subject: subject?.values[0]?.trim() || undefined,
    world: world?.values[0]?.trim() || undefined,
    instance: instance?.values[0]?.trim() || undefined,
    precision: precision?.values[0] as AuditRequest['precision'],
    hasActor: yesNo(chips, 'hasActor'),
    q: text?.values[0]?.trim() || undefined,
    from: when.from,
    to: when.to,
  }
}

/** The Audit log page at these chips, as an address a link can carry. */
export function auditAddress(chips: FilterChip[]): string {
  const params = new URLSearchParams()
  writeChips(params, chips)
  return `/audit?${params.toString()}`
}

/**
 * The chips that pin the audit log to one instance: its world and VRChat's number.
 *
 * Both, because the log records an instance by VRChat's number, which is only a number inside
 * one world: another world's #39047 is somebody else's evening. An instance whose number Modbot
 * never learned pins the world alone.
 */
export function instanceChips(instance: { worldId: string; vrChatInstanceId: string | null }): FilterChip[] {
  return [
    { property: 'world', operator: 'is', values: [instance.worldId] },
    ...(instance.vrChatInstanceId
      ? [{ property: 'instance', operator: 'is' as const, values: [instance.vrChatInstanceId] }]
      : []),
  ]
}

/**
 * The When chip for one instance's own run: the day it opened up to the day it closed, or from
 * the day it opened with no end while it is still open.
 *
 * VRChat hands a number out again once an instance closes, so the number alone would mix an
 * older instance's facts in. The chip is by the day, as the audit log's When chip is, so a number
 * reused within the same day in the same world is still not told apart here; the popup's Overview
 * reads the exact run.
 */
export function instanceRunChip(instance: { openedAt: string; closedAt: string | null }): FilterChip {
  const opened = utcDay(instance.openedAt)
  return instance.closedAt
    ? { property: 'when', operator: 'between', values: [opened, utcDay(instance.closedAt)] }
    : { property: 'when', operator: 'after', values: [opened] }
}

/** The UTC day an instant falls on, as the date chips write one: `dateRange` reads the days back as UTC. */
function utcDay(iso: string): string {
  return new Date(Date.parse(iso)).toISOString().slice(0, 10)
}

/**
 * The People page's default: nothing narrowed.
 *
 * The page is about finding somebody in the whole record, so it opens on the whole record and
 * every chip is something a moderator chose to add. The member list is this page with one chip,
 * `MEMBERS_VIEW`, which every link to "Members" carries in its address.
 */
export const PEOPLE_DEFAULTS: FilterChip[] = []

/** The People page narrowed to the group's current members: what the Members page was. */
export const MEMBERS_VIEW: FilterChip[] = [{ property: 'membership', operator: 'is', values: ['member'] }]

/** Which of the member list's two views the chips ask for, if either: the views with its columns. */
export function memberView(chips: FilterChip[]): 'member' | 'left' | null {
  const membership = chipFor(chips, 'membership')?.values[0]
  return membership === 'member' || membership === 'left' ? membership : null
}

export function peopleQueryFrom(chips: FilterChip[]): Omit<PeopleQuery, 'search' | 'sort' | 'page' | 'pageSize'> {
  const membership = chipFor(chips, 'membership')
  const profile = chipFor(chips, 'profile')
  const linked = chipFor(chips, 'linked')
  const rank = chipFor(chips, 'trustRank')
  const platform = chipFor(chips, 'platform')
  const role = chipFor(chips, 'role')
  const seen = dateRange(chips, 'seen')
  const joined = dateRange(chips, 'joined')

  return {
    membership: (membership?.values[0] as PeopleQuery['membership']) ?? 'all',
    banned: yesNo(chips, 'banned'),
    everBanned: yesNo(chips, 'everBanned'),
    profile: profile?.values[0] as PeopleQuery['profile'],
    eighteenPlus: yesNo(chips, 'eighteenPlus'),
    trustRanks: anyOf(rank),
    platforms: anyOf(platform),
    linked: (linked?.values[0] as PeopleQuery['linked']) ?? undefined,
    flagged: yesNo(chips, 'flagged'),
    seenFrom: seen.from,
    seenTo: seen.to,
    roles: role?.operator === 'is' && role.values.length ? role.values : undefined,
    notRoles: role?.operator === 'is-not' && role.values.length ? role.values : undefined,
    hasRole: yesNo(chips, 'hasRole'),
    representing: yesNo(chips, 'representing'),
    joinedFrom: joined.from,
    joinedTo: joined.to,
  }
}

/**
 * The values a choice chip asks for.
 *
 * Only "is any of": trust rank and platform are both unset for people nobody has read yet, and
 * "is not PC" turned into the rest of a list would quietly drop every one of them. The bar offers
 * neither property an "is not" for the same reason.
 */
function anyOf(chip: FilterChip | undefined): string[] | undefined {
  return chip?.operator === 'is' && chip.values.length ? chip.values : undefined
}

/** The Discord member list's default: people in the server. */
export const DISCORD_MEMBER_DEFAULTS: FilterChip[] = [{ property: 'state', operator: 'is', values: ['in-server'] }]

export function discordMemberQueryFrom(
  chips: FilterChip[],
): Omit<DiscordMemberQuery, 'search' | 'sort' | 'page' | 'pageSize'> {
  const role = chipFor(chips, 'role')
  const state = chipFor(chips, 'state')
  const linked = chipFor(chips, 'linked')
  const joined = dateRange(chips, 'joined')

  return {
    roles: role?.operator === 'is' && role.values.length ? role.values : undefined,
    notRoles: role?.operator === 'is-not' && role.values.length ? role.values : undefined,
    hasRole: yesNo(chips, 'hasRole'),
    state: (state?.values[0] as DiscordMemberQuery['state']) ?? 'all',
    linked: (linked?.values[0] as DiscordMemberQuery['linked']) ?? undefined,
    bot: yesNo(chips, 'bot'),
    pending: yesNo(chips, 'pending'),
    timedOut: yesNo(chips, 'timedOut'),
    boosting: yesNo(chips, 'boosting'),
    joinedFrom: joined.from,
    joinedTo: joined.to,
  }
}
