import type { LivePerson, LiveTally } from '@/lib/api'

/** How long somebody who walked in is marked New (UX review 2026-09-25, finding 16). */
export const NEW_MS = 5 * 60_000

/** And how long their row is lit as well, so an arrival catches the eye and then settles. */
export const LIT_MS = 60_000

/**
 * Whether a moderator saw this person walk in less than `ms` ago. `now` is the server's time, so a
 * browser whose clock is wrong still marks the right people; left out, nobody is new.
 */
export function arrivedWithin(p: Pick<LivePerson, 'arrivedAt'>, now: number | undefined, ms: number): boolean {
  if (p.arrivedAt === null || now === undefined) return false
  const age = now - Date.parse(p.arrivedAt)
  return age >= 0 && age < ms
}

/** Flagged, or kicked or banned before: the people a moderator is watching for. */
export function watchFor(p: Pick<LivePerson, 'standing' | 'priorActions'>): boolean {
  return p.standing === 'Flagged' || p.priorActions > 0
}

/**
 * The people a moderator is watching for first, where a glance lands, rather than wherever their
 * arrival put them. Otherwise the server's order, which a stable sort keeps.
 */
export function pinned<T extends Pick<LivePerson, 'standing' | 'priorActions'>>(people: T[]): T[] {
  return [...people].sort((a, b) => Number(watchFor(b)) - Number(watchFor(a)))
}

/** The running line's counts in order, each with its word: `[212, 'arrivals'], [1, 'ban']`. */
export function tallyCounts(tally: LiveTally): [number, string][] {
  const counts: [number, string][] = [
    [tally.arrivals, 'arrival'],
    [tally.warns, 'warn'],
    [tally.kicks, 'kick'],
    [tally.bans, 'ban'],
  ]
  return counts.map(([n, word]) => [n, n === 1 ? word : `${word}s`])
}
