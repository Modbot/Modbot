// The words, the shapes and the small decisions the rule builder is made of -- and nothing that
// talks to the server, so the Node test runner can load this file as it is. The same split
// `nav.ts` and `permissions.ts` use, and for the same reason.
import type { Giveaway, GiveawayEntryWay, GiveawayInput, GiveawayPostState, GiveawayState } from './giveaways.ts'

export const STATE_LABEL: Record<GiveawayState, string> = {
  draft: 'Draft',
  open: 'Open',
  closed: 'Closed',
  drawn: 'Drawn',
  cancelled: 'Cancelled',
}

export const POST_STATE_LABEL: Record<GiveawayPostState, string> = {
  waiting: 'Waiting',
  published: 'Published',
  failed: 'Failed',
  removed: 'Removed',
}

export const ENTRY_WAY_LABEL: Record<GiveawayEntryWay, string> = {
  automatic: 'Everyone who matches',
  react: 'React on Discord',
}

export const WEIGHTING_LABEL: Record<string, string> = {
  uniform: 'Everybody the same',
  instanceHours: 'Hours in our instances',
  voiceHours: 'Hours in Discord voice',
  messages: 'Discord messages',
  daysSeen: 'Days seen',
}

/** The combining kinds, in the order the builder offers them. */
export const COMBINE_LABEL: Record<string, string> = {
  allOf: 'All of',
  anyOf: 'Any of',
  noneOf: 'None of',
}

export const RULE_LABEL: Record<string, string> = {
  discordMemberDays: 'In Discord for at least',
  groupMemberDays: 'In the group for at least',
  inGroup: 'In the group now',
  instanceHours: 'Hours in our instances, at least',
  oneInstanceHours: 'Hours in one single instance, at least',
  voiceHours: 'Hours in Discord voice, at least',
  messages: 'Discord messages sent, at least',
  seenWithinDays: 'Seen in the last',
  linkedAccounts: 'Discord and VRChat accounts linked',
  groupRole: 'Holds the group role',
  discordRole: 'Holds the Discord role',
  noTrouble: 'No bans, kicks or flags',
  vrchatAccountDays: 'VRChat account at least',
  trustRankAtLeast: 'Trust rank at least',
  age18Plus: '18+ verified',
}

/** The words VRChat's nameplate shows for each trust rank a rule can ask for. */
export const TRUST_RANK_LABEL: Record<string, string> = {
  Visitor: 'Visitor',
  NewUser: 'New User',
  User: 'User',
  KnownUser: 'Known User',
  TrustedUser: 'Trusted User',
  Legend: 'Legend',
}

/** The ranks in ladder order, for a server that did not send its own list. */
export const TRUST_RANKS = Object.keys(TRUST_RANK_LABEL)

/** The unit shown after a rule's number, or an empty string for a rule with none. */
export const RULE_UNIT: Record<string, string> = {
  discordMemberDays: 'days',
  groupMemberDays: 'days',
  instanceHours: 'hours',
  oneInstanceHours: 'hours',
  voiceHours: 'hours',
  messages: 'messages',
  seenWithinDays: 'days',
  vrchatAccountDays: 'days',
}

export const COMBINING = ['allOf', 'anyOf', 'noneOf']

export function isCombining(kind: string): boolean {
  return COMBINING.includes(kind)
}

export function takesAmount(kind: string): boolean {
  return kind in RULE_UNIT
}

export function takesWindow(kind: string): boolean {
  return ['instanceHours', 'oneInstanceHours', 'voiceHours', 'messages', 'noTrouble'].includes(kind)
}

export function takesRole(kind: string): boolean {
  return kind === 'groupRole' || kind === 'discordRole'
}

/** Whether a rule names a trust rank. Uses the same field a role does, with its own picker. */
export function takesRank(kind: string): boolean {
  return kind === 'trustRankAtLeast'
}

/** Whether a rule's answer comes from polled presence reports rather than exactly-timed facts. */
export function fromPolledData(kind: string): boolean {
  return ['instanceHours', 'oneInstanceHours', 'seenWithinDays'].includes(kind)
}

/** `2026-09-20T20:00` for a Date, in the browser's own time — what a datetime-local input holds. */
export function localInputValue(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

/** An instant back as the local value a datetime-local input wants. */
export function toLocalInput(iso: string | null): string {
  return iso ? localInputValue(new Date(iso)) : ''
}

/** A datetime-local value back as an instant. */
export function fromLocalInput(value: string): string {
  return new Date(value).toISOString()
}

/** A blank giveaway: opens now, closes in a week, one winner, everyone in. */
export function blankGiveaway(now: Date): GiveawayInput {
  const closes = new Date(now)
  closes.setDate(closes.getDate() + 7)

  return {
    name: '',
    prize: '',
    opensAt: now.toISOString(),
    closesAt: closes.toISOString(),
    drawAt: null,
    winnerCount: 1,
    entryWay: 'automatic',
    emoji: '🎉',
    rules: { kind: 'allOf', rules: [] },
    exclusions: { staff: false, pastWinners: false, bannedMembers: false, people: [] },
    weighting: 'uniform',
    weightCap: null,
    postToChannel: false,
    channelId: null,
    draft: false,
  }
}

export function inputFrom(giveaway: Giveaway): GiveawayInput {
  return {
    name: giveaway.name,
    prize: giveaway.prize,
    opensAt: giveaway.opensAt,
    closesAt: giveaway.closesAt,
    drawAt: giveaway.drawAt,
    winnerCount: giveaway.winnerCount,
    entryWay: giveaway.entryWay,
    emoji: giveaway.emoji,
    rules: giveaway.rules,
    exclusions: giveaway.exclusions,
    weighting: giveaway.weighting,
    weightCap: giveaway.weightCap,
    postToChannel: giveaway.postToChannel,
    channelId: giveaway.channelId,
    draft: giveaway.state === 'draft',
  }
}

/**
 * A number of hours or days as the page shows it.
 *
 * Presence figures are sampled by whichever moderator's companion happened to be in the instance,
 * so one is shown as "about 12" and an exactly-counted one as "12" (M7 §2.3). Printing a polled
 * figure to a decimal place would be inventing precision nobody measured.
 */
export function measured(value: number, polled: boolean): string {
  const rounded = polled ? Math.round(value) : Math.round(value * 10) / 10
  return polled ? `about ${rounded}` : String(rounded)
}
