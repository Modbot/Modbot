// An event's pictures in the form (calendar design §15.1–§15.3): the VRChat file id inside a pasted
// link, the VRChat picture that follows a picture link from VRChat, and the crop box's arithmetic.
// Nothing here talks to the server or the page, so the Node test runner can load this file as it is.

/** The shape the refusal shows, the same as the server's `VRChatFileIds.Example`. */
export const VRCHAT_FILE_EXAMPLE = 'file_1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d'

/** The sentence for text with no file id in it, the same as the server's `VRChatFileIds.NotFound`. */
export const VRCHAT_FILE_NOT_FOUND = `Use a VRChat file link or id, like ${VRCHAT_FILE_EXAMPLE}.`

/**
 * VRChat's event pictures are 16:9: an event with no picture of its own shows VRChat's 1024 × 576 one.
 * Discord's event cover is 2.5:1, the 800 × 320 Discord asks for.
 */
export const VRCHAT_PICTURE_ASPECT = 16 / 9
export const DISCORD_COVER_ASPECT = 2.5

/** The widest picture the crop box makes. VRChat shows events far smaller; this keeps the file small. */
export const CROP_MAX_WIDTH = 2048

/** `vrchat.cloud`, `vrchat.com`, or anything under either. */
export function isVRChatHost(host: string): boolean {
  const h = host.toLowerCase().replace(/\.$/, '')
  return h === 'vrchat.cloud' || h.endsWith('.vrchat.cloud') || h === 'vrchat.com' || h.endsWith('.vrchat.com')
}

/** `vrchat.cloud` or anything under it: where VRChat serves its files, to a signed-in session only. */
export function isVRChatFileHost(host: string): boolean {
  const h = host.toLowerCase().replace(/\.$/, '')
  return h === 'vrchat.cloud' || h.endsWith('.vrchat.cloud')
}

function parseLink(text: string): URL | null {
  try {
    const url = new URL(text)
    return url.protocol === 'http:' || url.protocol === 'https:' ? url : null
  } catch {
    return null
  }
}

const USUAL_FORM = /file_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i
const ANY_FORM = /file_[A-Za-z0-9_-]+/

/**
 * The VRChat file id in what somebody pasted: the id, or any VRChat link with one in it. Null when
 * there is none, or the link is somebody else's. The server's `VRChatFileIds.Find`, word for word:
 * an id in VRChat's usual form is taken exactly (which drops a `_blob` or a version after it), and
 * any other `file_…` up to the next character that cannot be in an id. Ids are never judged by
 * their shape (foundation §3.1.1).
 */
export function vrchatFileIdIn(text: string | null | undefined): string | null {
  const trimmed = text?.trim()
  if (!trimmed) return null

  const link = parseLink(trimmed)
  if (link && !isVRChatHost(link.hostname)) return null

  return USUAL_FORM.exec(trimmed)?.[0] ?? ANY_FORM.exec(trimmed)?.[0] ?? null
}

/** The file id in a picture link, only when the link is on one of VRChat's hosts. */
export function vrchatFileIdInLink(link: string | null | undefined): string | null {
  const parsed = link ? parseLink(link.trim()) : null
  return parsed && isVRChatHost(parsed.hostname) ? vrchatFileIdIn(link) : null
}

/**
 * The VRChat picture once the picture link changes from `was` to `now` (§15.1). A link from VRChat
 * fills the VRChat picture, and a VRChat picture that came from the old link follows the new one,
 * or empties when the new one is not VRChat's. A VRChat picture chosen any other way stays.
 */
export function pictureFollowingLink(was: string | null, now: string | null, current: string | null): string | null {
  const before = vrchatFileIdInLink(was)
  const after = vrchatFileIdInLink(now)

  if (current === null) return after
  if (before !== null && current === before) return after
  return current
}

/** A crop, in the picture's own pixels. */
export type CropBox = { x: number; y: number; width: number; height: number }

/** The largest box of `aspect` (width ÷ height) that fits, in the middle: what VRChat and Discord do themselves. */
export function centredCrop(width: number, height: number, aspect: number): CropBox {
  const boxWidth = Math.min(width, height * aspect)
  const boxHeight = boxWidth / aspect
  return { x: (width - boxWidth) / 2, y: (height - boxHeight) / 2, width: boxWidth, height: boxHeight }
}

/** The box moved by `dx`, `dy` picture pixels, kept inside the picture. */
export function moveCrop(box: CropBox, dx: number, dy: number, width: number, height: number): CropBox {
  return {
    ...box,
    x: clamp(box.x + dx, 0, width - box.width),
    y: clamp(box.y + dy, 0, height - box.height),
  }
}

/** The smallest a box gets, as a share of the largest one. */
export const CROP_SMALLEST = 0.2

/**
 * The box at `size` (from `CROP_SMALLEST` to 1) of the largest box of its shape, around the same
 * middle, kept inside the picture.
 */
export function sizeCrop(box: CropBox, size: number, width: number, height: number, aspect: number): CropBox {
  const largest = centredCrop(width, height, aspect)
  const share = clamp(size, CROP_SMALLEST, 1)
  const boxWidth = largest.width * share
  const boxHeight = boxWidth / aspect
  const middleX = box.x + box.width / 2
  const middleY = box.y + box.height / 2

  return {
    x: clamp(middleX - boxWidth / 2, 0, width - boxWidth),
    y: clamp(middleY - boxHeight / 2, 0, height - boxHeight),
    width: boxWidth,
    height: boxHeight,
  }
}

/** How large the box is, as a share of the largest box of its shape. */
export function cropSize(box: CropBox, width: number, height: number, aspect: number): number {
  return box.width / centredCrop(width, height, aspect).width
}

/** The size of the picture a crop makes: never wider than `CROP_MAX_WIDTH`, never enlarged. */
export function croppedSize(box: CropBox, aspect: number): { width: number; height: number } {
  const width = Math.max(1, Math.round(Math.min(box.width, CROP_MAX_WIDTH)))
  return { width, height: Math.max(1, Math.round(width / aspect)) }
}

function clamp(value: number, low: number, high: number): number {
  return Math.min(Math.max(value, low), Math.max(low, high))
}
