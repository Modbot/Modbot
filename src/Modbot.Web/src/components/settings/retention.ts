/**
 * Whether saving `next` in place of `current` makes Modbot delete records it keeps today.
 *
 * A window is in days, and 0 keeps forever. Going from forever to any number of days, or from
 * more days to fewer, shortens a window, and the records past the new end are destroyed for good.
 * A longer window, or a blank box read as 0, deletes nothing. Settings asks before a save that
 * shortens one (settings review 2026-09-27 §4).
 */
export function shortensAny(current: readonly number[], next: readonly number[]): boolean {
  return current.some((was, i) => {
    const now = next[i] ?? 0
    return now > 0 && (was === 0 || now < was)
  })
}
