// Relative, with the extension, so the Node test runner can load this file as it is (see nav.ts).
// Every import is either a type, which is stripped, or nav.ts, which loads on its own.
import type { CurrentUser } from './api.ts'
import { mayOpen, type PageId } from './nav.ts'

/**
 * The pieces of the Discord page that are worth a test: which links a person sees, whether they
 * see who is in voice, the boost bar,
 * the size asked of Discord's picture site, the week's ups and downs, an account's age read from
 * its id, and which picture a channel is drawn with.
 */

/**
 * The row under the server's header, named and ordered the way Discord's own server column names
 * its parts -- Events above Members -- each leading to the Modbot page that shows that part.
 * Overview is the page itself.
 *
 * There is no voice link: who is in voice is drawn on the page itself, under the header, as
 * Discord draws it in its channel list rather than in its server menu.
 *
 * "Events" has no count. Discord's column says "5 Events" from Discord's own event list, which the
 * bot does not read; a count from Modbot's calendar would sit beside Discord's and disagree with it
 * (Discord page review, 2026-09-27 decisions).
 */
export const SERVER_TABS: readonly { id: PageId; label: string }[] = [
  { id: 'analytics-server', label: 'Overview' },
  { id: 'calendar', label: 'Events' },
  // Discord's column has "Channels & Roles" between Events and Members; here they are two links,
  // each a read-only report (Discord tidy-up design).
  { id: 'discord-channels', label: 'Channels' },
  { id: 'discord-roles', label: 'Roles' },
  { id: 'discord-members', label: 'Members' },
  { id: 'bans', label: 'Bans' },
]

/** The links this person may open, in order: a page the sidebar hides is not offered here either. */
export function serverTabs(me: CurrentUser): { id: PageId; label: string }[] {
  return SERVER_TABS.filter((tab) => mayOpen(me, tab.id))
}

/**
 * The address a link of the row leads to. Bans opens the Bans page on its Discord list, with
 * `from=server` so that page draws this header over itself and the person has not left the Discord
 * page; everything else is `path` as it is.
 */
export function serverTabHref(id: PageId, path: string): string {
  return id === 'bans' ? `${path}${path.includes('?') ? '&' : '?'}platform=discord&from=server` : path
}

/**
 * The link to mark when `page` was opened from the server's row, or null when it was not. Only
 * Bans is drawn under the server's header that way.
 */
export function serverTabFrom(page: PageId, search: URLSearchParams): PageId | null {
  return search.get('from') === 'server' && page === 'bans' ? 'bans' : null
}

/**
 * Whether the page draws who is in voice. It is read from Live's own answer, so it is shown to
 * whoever may open Live and to nobody else.
 */
export function seesVoice(me: CurrentUser): boolean {
  return mayOpen(me, 'live')
}

/** The boosts each level needs: level 1 at 2, level 2 at 7, level 3 at 14. Discord's own numbers. */
export const BOOSTS_FOR_LEVEL = [2, 7, 14] as const

export type BoostGoal = {
  boosts: number
  level: number
  /** The boosts the next level needs, or null at level 3, where there is no next one. */
  goal: number | null
}

/**
 * The boost bar: how many boosts, the level, and the boosts the next level needs. This is the
 * bar Discord draws in its own server menu; Discord has no goal a server sets for itself.
 *
 * The level is Discord's when it gave one, because Discord lets a server keep its level for a
 * while after boosts run out; otherwise it is worked out from the count. Null when the count is
 * not known.
 */
export function boostGoal(boosts: number | null, level: number | null): BoostGoal | null {
  if (boosts === null || !Number.isFinite(boosts) || boosts < 0) return null

  const count = Math.floor(boosts)
  const fromCount = BOOSTS_FOR_LEVEL.filter((needed) => count >= needed).length
  const known = level !== null && Number.isInteger(level) && level >= 0 && level <= 3 ? level : fromCount

  return { boosts: count, level: known, goal: known < 3 ? BOOSTS_FOR_LEVEL[known] : null }
}

/** How full the boost bar is, 0 to 1. Full at level 3. */
export function boostShare(goal: BoostGoal): number {
  if (goal.goal === null) return 1
  return Math.min(1, goal.boosts / goal.goal)
}

/**
 * What Discord draws in place of a server icon: the first letter of each word, "The Black Cat"
 * → "TBC". Up to three, so a long name still fits the square. Null for a name with no letters.
 */
export function serverInitials(name: string | null): string | null {
  if (!name) return null

  const letters = name
    .split(/\s+/)
    .map((word) => Array.from(word.replace(/[^\p{L}\p{N}]/gu, ''))[0])
    .filter((first): first is string => first !== undefined)

  return letters.length > 0 ? letters.slice(0, 3).join('') : null
}

/**
 * A picture on Discord's picture site asked for at a size, so an icon is not fetched at full size
 * to be drawn at 96 pixels. Discord takes powers of two from 16 to 4096. Any other address is
 * returned as it is, and only an `https` one is returned at all.
 */
export function discordPicture(url: string | null, size: number): string | null {
  if (!url) return null

  try {
    const parsed = new URL(url)
    if (parsed.protocol !== 'https:') return null
    if (parsed.hostname === 'cdn.discordapp.com' || parsed.hostname === 'media.discordapp.net') {
      parsed.searchParams.set('size', String(size))
    }
    return parsed.toString()
  } catch {
    return null
  }
}

/**
 * How one of the week's numbers moved against the week before: the difference and which way.
 * "Same" when it did not move, so a flat week is not drawn as a fall.
 */
export function weekChange(pair: { thisWeek: number; lastWeek: number }): { by: number; way: 'up' | 'down' | 'same' } {
  const by = pair.thisWeek - pair.lastWeek
  return { by: Math.abs(by), way: by > 0 ? 'up' : by < 0 ? 'down' : 'same' }
}

/** The first moment of 2015, in milliseconds: where Discord starts counting its ids from. */
const DISCORD_EPOCH_MS = 1_420_070_400_000

/**
 * When a Discord account was made, read from its id: the id's top bits are milliseconds since the
 * start of 2015, the same way the header reads "Est." from the server's id. Null for an id that is
 * not a number; Modbot never checks an id's shape, it only declines to read a date out of one.
 */
export function accountMadeAt(id: string): number | null {
  if (!/^\d+$/.test(id)) return null

  try {
    return Number(BigInt(id) >> BigInt(22)) + DISCORD_EPOCH_MS
  } catch {
    return null
  }
}

/** Under this many days, an account counts as new, as Discord's own Insights counts it. */
export const NEW_ACCOUNT_DAYS = 30

/**
 * A Discord account's age in Modbot's units against the server's clock: "12d", "7mth", "4y".
 * `fresh` when it is under {@link NEW_ACCOUNT_DAYS}. Null when the id carries no date.
 */
export function accountAge(id: string, nowIso: string): { text: string; fresh: boolean } | null {
  const made = accountMadeAt(id)
  const now = Date.parse(nowIso)
  if (made === null || !Number.isFinite(now)) return null

  const days = Math.max(0, Math.floor((now - made) / 86_400_000))
  return { text: days < 1 ? 'today' : span(days, made, now), fresh: days < NEW_ACCOUNT_DAYS }
}

/**
 * How long ago something was, in the steps of Discord's Members page ("today", then days, months,
 * years) and Modbot's units: "today", "11d ago", "7mth ago", "4y ago". Against the server's
 * clock. Null when either time is unreadable.
 */
export function timeAgo(iso: string, nowIso: string): string | null {
  const at = Date.parse(iso)
  const now = Date.parse(nowIso)
  if (!Number.isFinite(at) || !Number.isFinite(now)) return null

  const days = Math.max(0, Math.floor((now - at) / 86_400_000))
  return days < 1 ? 'today' : `${span(days, at, now)} ago`
}

/**
 * The time from `fromMs` to `toMs` in the largest unit that reads well: days under two months, then
 * months, then years. Months are calendar months (27 Feb to 27 Sep is 7), the way Discord counts
 * them; days divided by an average month reads 6 for that same stretch.
 */
function span(days: number, fromMs: number, toMs: number): string {
  if (days < 60) return `${days}d`

  const from = new Date(fromMs)
  const to = new Date(toMs)
  let months = (to.getUTCFullYear() - from.getUTCFullYear()) * 12 + to.getUTCMonth() - from.getUTCMonth()

  // The last month counts only once it is complete.
  const lastWhole = new Date(fromMs)
  lastWhole.setUTCMonth(from.getUTCMonth() + months)
  if (lastWhole.getTime() > toMs) months -= 1
  if (months < 12) return `${months}mth`

  return `${Math.floor(months / 12)}y`
}

/** Which of Discord's channel pictures a stored channel kind is drawn with. */
export type ChannelLook = 'text' | 'voice' | 'forum' | 'announcement' | 'stage'

export function channelLook(type: string | null): ChannelLook {
  switch (type) {
    case 'voice':
      return 'voice'
    case 'stage':
      return 'stage'
    case 'forum':
    case 'media':
      return 'forum'
    case 'announcement':
      return 'announcement'
    default:
      return 'text'
  }
}
