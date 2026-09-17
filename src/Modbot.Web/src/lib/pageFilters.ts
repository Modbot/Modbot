import type { AuditRequest, DiscordMemberQuery, MemberQuery } from './api.ts'
import { chipFor, dateRange, yesNo, type FilterChip } from './filters.ts'

/**
 * What each page's chips ask the server. Pure, so a test can check that a chip becomes the
 * query parameter it should, without a browser.
 */

/** The audit log's default: VRChat, Discord and Client on, Sync off, so a sweep's noticed changes do not crowd the exact entries. */
export const AUDIT_DEFAULTS: FilterChip[] = [{ property: 'source', operator: 'is', values: ['AuditLog', 'Discord', 'Client'] }]

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
  const SOURCES = ['AuditLog', 'SyncDiff', 'Client', 'Discord', 'Manual', 'Modbot']

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
