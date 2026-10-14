/** What the catch-up needs from the outside, so it can be tried without a server or a canvas. */
export interface CoverCopyParts {
  /** The ids of covers that still need a Bluesky copy, leaving out the ones in `skip`. */
  missing: (skip: string[]) => Promise<string[]>
  /** The cover's picture as it was kept. */
  picture: (coverId: string) => Promise<Blob>
  /** The small JPEG made from it. */
  smallCopy: (picture: Blob) => Promise<Blob>
  /** Keeps the copy on the cover. */
  keep: (coverId: string, copy: Blob) => Promise<unknown>
  /** True once the page is gone or someone else is signed in: no more covers are started. */
  stopped: () => boolean
}

/** Asks per visit; the server lists 20 each, so this is at most 100 covers. */
export const COVER_COPY_ROUNDS = 5

/** The most failed ids sent as `skip`, the server's `MaxSkippedCoversNeedingBlueskyCopy`. */
export const COVER_COPY_MAX_SKIP = 100

/**
 * The covers that failed since the page was opened. They are left out of the next asks, so one the
 * browser cannot convert does not sit at the front of the list and hide the rest; a reload tries them
 * again. A kept cover is not here: the server stops listing it.
 */
const failed = new Set<string>()

/**
 * Covers chosen before the Bluesky copy existed have none, and a cover over 1 MB then posts with only
 * the link card. This makes the missing copies one at a time, in the browser, with the same code a new
 * cover uses. Silent: a cover that fails is skipped and tried again after a reload. Asks again after
 * each batch of 20, at most `COVER_COPY_ROUNDS` times, so a visit does a bounded amount of work.
 * Answers with how many copies were kept. `skipped` holds the failed ids; the page's own set is the
 * default.
 */
export async function makeMissingCoverCopies(parts: CoverCopyParts, skipped: Set<string> = failed): Promise<number> {
  let kept = 0

  for (let round = 0; round < COVER_COPY_ROUNDS; round++) {
    let ids: string[]

    try {
      ids = await parts.missing([...skipped].slice(0, COVER_COPY_MAX_SKIP))
    } catch {
      return kept
    }

    if (ids.length === 0) return kept

    for (const id of ids) {
      if (parts.stopped()) return kept

      try {
        await parts.keep(id, await parts.smallCopy(await parts.picture(id)))
        kept++
      } catch {
        skipped.add(id)
      }
    }
  }

  return kept
}
