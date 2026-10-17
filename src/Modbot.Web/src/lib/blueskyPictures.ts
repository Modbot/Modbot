import type { BlueskyPicture } from './calendar.ts'

/** The most pictures one Bluesky post carries. The server's `CalendarBlueskyText.MostPictures`. */
export const BLUESKY_PICTURES_MAX = 4

/** The longest alt text a picture keeps: the server's `CalendarBlueskyText.AltMaxLength`. */
export const BLUESKY_ALT_MAX = 2000

/**
 * The pictures the form shows for Bluesky's post: its own list when it has one, else the cover alone
 * (what the post is sent while no list is saved), else none.
 */
export function shownPictures(list: BlueskyPicture[] | null | undefined, coverPictureId: string | null | undefined): BlueskyPicture[] {
  if (list) return list
  return coverPictureId ? [{ pictureId: coverPictureId, alt: '' }] : []
}

/** `list` with `added` after it, cut at the room left. */
export function withPictures(list: BlueskyPicture[], added: BlueskyPicture[]): BlueskyPicture[] {
  return [...list, ...added].slice(0, BLUESKY_PICTURES_MAX)
}

/** `list` with the picture at `index` changed by `change`; an index outside it changes nothing. */
export function withPictureChanged(list: BlueskyPicture[], index: number, change: Partial<BlueskyPicture>): BlueskyPicture[] {
  return list.map((picture, i) => (i === index ? { ...picture, ...change } : picture))
}

/** `list` without the picture at `index`. */
export function withoutPicture(list: BlueskyPicture[], index: number): BlueskyPicture[] {
  return list.filter((_, i) => i !== index)
}
