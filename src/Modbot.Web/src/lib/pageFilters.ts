import type { AuditRequest, DiscordMemberQuery, MemberQuery, PeopleQuery } from './api.ts'
import { chipFor, dateRange, yesNo, type FilterChip } from './filters.ts'

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
  { property: 'source', operator: 'is', values: ['AuditLog', 'Discord', 'Client', 'Import'] },
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
  const SOURCES = ['AuditLog', 'SyncDiff', 'Client', 'Discord', 'Manual', 'Modbot', 'Import']

  return {
    source:
      source?.operator === 'is-not'
        ? SOURCES.filter((s) => !source.values.includes(s))
        : source?.values.length
          ? source.values
          : undefined,
    type: type?.values.length ? type.values : undefined,
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

/** The member list's default: current members. */
export const MEMBER_DEFAULTS: FilterChip[] = [{ property: 'status', operator: 'is', values: ['current'] }]

export function memberQueryFrom(chips: FilterChip[]): Omit<MemberQuery, 'search' | 'sort' | 'page' | 'pageSize'> {
  const role = chipFor(chips, 'role')
  const status = chipFor(chips, 'status')
  const linked = chipFor(chips, 'linked')
  const profile = chipFor(chips, 'profile')
  const joined = dateRange(chips, 'joined')
  const seen = dateRange(chips, 'seen')

  return {
    roles: role?.operator === 'is' && role.values.length ? role.values : undefined,
    notRoles: role?.operator === 'is-not' && role.values.length ? role.values : undefined,
    hasRole: yesNo(chips, 'hasRole'),
    status: (status?.values[0] as MemberQuery['status']) ?? 'all',
    linked: (linked?.values[0] as MemberQuery['linked']) ?? undefined,
    eighteenPlus: yesNo(chips, 'eighteenPlus'),
    representing: yesNo(chips, 'representing'),
    profile: profile?.values[0] as MemberQuery['profile'],
    joinedFrom: joined.from,
    joinedTo: joined.to,
    seenFrom: seen.from,
    seenTo: seen.to,
  }
}

/**
 * The People page's default: nothing narrowed.
 *
 * The member list opens on current members because that is what its page is about. This page is
 * about finding somebody in the whole record, so it opens on the whole record and every chip is
 * something a moderator chose to add.
 */
export const PEOPLE_DEFAULTS: FilterChip[] = []

export function peopleQueryFrom(chips: FilterChip[]): Omit<PeopleQuery, 'search' | 'sort' | 'page' | 'pageSize'> {
  const membership = chipFor(chips, 'membership')
  const profile = chipFor(chips, 'profile')
  const linked = chipFor(chips, 'linked')
  const rank = chipFor(chips, 'trustRank')
  const platform = chipFor(chips, 'platform')
  const seen = dateRange(chips, 'seen')

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
