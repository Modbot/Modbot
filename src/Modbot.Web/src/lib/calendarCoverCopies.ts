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

/**
 * The covers tried since the page was opened, kept or not. A cover that failed is left out of the next
 * asks, so one the browser cannot convert does not sit at the front of the list and hide the rest; a
 * reload tries it again.
 */
const tried = new Set<string>()

/**
 * Covers chosen before the Bluesky copy existed have none, and a cover over 1 MB then posts with only
 * the link card. This makes the missing copies one at a time, in the browser, with the same code a new
 * cover uses. Silent: a cover that fails is skipped and tried again after a reload. Asks again after
 * each batch (the server lists 20 at most) until nothing is left. Answers with how many copies were
 * kept. `already` holds the ids to leave out; the page's own set is the default.
 */
export async function makeMissingCoverCopies(parts: CoverCopyParts, already: Set<string> = tried): Promise<number> {
  let kept = 0

  for (;;) {
    let ids: string[]

    try {
      ids = await parts.missing([...already])
    } catch {
      return kept
    }

    // Each id is added below whatever happens to it, so the next ask is a different list.
    const fresh = ids.filter((id) => !already.has(id))
    if (fresh.length === 0) return kept

    for (const id of fresh) {
      if (parts.stopped()) return kept

      already.add(id)

      try {
        await parts.keep(id, await parts.smallCopy(await parts.picture(id)))
        kept++
      } catch {
        // Left for after a reload.
      }
    }
  }
}
