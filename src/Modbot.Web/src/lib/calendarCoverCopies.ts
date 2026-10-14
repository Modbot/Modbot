/** What the catch-up needs from the outside, so it can be tried without a server or a canvas. */
export interface CoverCopyParts {
  /** The ids of covers that still need a Bluesky copy. */
  missing: () => Promise<string[]>
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
 * Covers chosen before the Bluesky copy existed have none, and a cover over 1 MB then posts with only
 * the link card. This makes the missing copies one at a time, in the browser, with the same code a new
 * cover uses. Silent: a cover that fails is skipped and tried again on the next visit. Answers with
 * how many copies were kept.
 */
export async function makeMissingCoverCopies(parts: CoverCopyParts): Promise<number> {
  let ids: string[]

  try {
    ids = await parts.missing()
  } catch {
    return 0
  }

  let kept = 0

  for (const id of ids) {
    if (parts.stopped()) break

    try {
      await parts.keep(id, await parts.smallCopy(await parts.picture(id)))
      kept++
    } catch {
      // Left for the next visit.
    }
  }

  return kept
}
