// Relative, with the extension, so the Node test runner can load this file as it is (see nav.ts).
// Every import is either a type, which is stripped, or nav.ts, which loads on its own.
import type { CurrentUser } from './api.ts'
import { mayOpen, type PageId } from './nav.ts'

/**
 * The pieces of the Discord analytics page's header that are worth a test: which links a person
 * sees, the boost bar, and the size asked of Discord's picture site.
 */

/**
 * The row under the server's header, named the way Discord's own server menu names its parts,
 * each leading to the Modbot page that shows that part. Overview is the page itself.
 */
export const SERVER_TABS: readonly { id: PageId; label: string }[] = [
  { id: 'analytics-server', label: 'Overview' },
  { id: 'discord-members', label: 'Members' },
  { id: 'live', label: 'Voice now' },
  { id: 'calendar', label: 'Events' },
  { id: 'bans', label: 'Bans' },
]

/** The links this person may open, in order: a page the sidebar hides is not offered here either. */
export function serverTabs(me: CurrentUser): { id: PageId; label: string }[] {
  return SERVER_TABS.filter((tab) => mayOpen(me, tab.id))
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
