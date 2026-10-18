// The PC, Android and iOS boxes of the VRChat tab and the event picture they are drawn into (calendar
// design §15.5, 2026-10-17). Nothing here talks to the server or the page, so the Node test runner can
// load this file as it is.

/**
 * The boxes a world's builds tick: the platforms the form offers that the world has a build for, in
 * the form's order. Null when the world's builds are not known, or none of them is one the form
 * offers: the boxes stay as they are.
 */
export function platformsFromWorld(
  worldPlatforms: readonly string[] | null | undefined,
  offered: readonly string[],
): string[] | null {
  if (!worldPlatforms || worldPlatforms.length === 0) return null

  const ticked = offered.filter((p) => worldPlatforms.includes(p))
  return ticked.length > 0 ? ticked : null
}

/** The same text for the same ticks, whatever order they were made in: two keys differ when the picture has to be drawn again. */
export function platformsKey(platforms: readonly string[]): string {
  return [...platforms].sort().join(',')
}

/**
 * Where the picked world's own picture is fetched from: its full-size picture, and its small one to
 * fall back on when the full-size one cannot be fetched. Null when the world has neither.
 */
export function worldPictureLinks(
  world: { imageUrl?: string | null; thumbnailUrl?: string | null } | undefined,
): { link: string; fallback: string | null } | null {
  const full = world?.imageUrl || null
  const small = world?.thumbnailUrl || null

  if (full) return { link: full, fallback: small && small !== full ? small : null }
  return small ? { link: small, fallback: null } : null
}

/** Whether the world picked now is the one whose own picture was taken off with Remove. */
export function worldPictureRemoved(removedFor: string | null, worldId: string | null): boolean {
  return removedFor !== null && removedFor === worldId
}

/**
 * Whether Save uses the picked world's own picture as the VRChat picture: the event goes to VRChat,
 * has no picture, no crop waiting and no picture link (a link was a picture chosen, even if its crop
 * was cancelled), the world has a picture, and a picture may be uploaded. A draft and a template go
 * nowhere, so they upload nothing on their own. Remove on the picture stops it for that world, until
 * a picture is chosen or another world is picked.
 */
export function usesWorldPicture(state: {
  vrChatImageId: string | null
  cropWaiting: boolean
  pictureLink: string | null
  worldPictureUrl: string | null | undefined
  /** The world picked now. */
  worldId: string | null
  /** The world whose picture was removed in this form, or null. */
  removedFor: string | null
  uploads: boolean
  /** Whether Modbot's VRChat account has VRChat+; null is not known. */
  plus: boolean | null
  publishToVRChat: boolean
  draft: boolean
  template: boolean
}): boolean {
  return (
    !state.vrChatImageId &&
    !state.cropWaiting &&
    !state.pictureLink &&
    !!state.worldPictureUrl &&
    !worldPictureRemoved(state.removedFor, state.worldId) &&
    state.uploads &&
    state.plus !== false &&
    state.publishToVRChat &&
    !state.draft &&
    !state.template
  )
}

/** Where the icons sit on a picture `width` wide: top right, the last one in the corner, as the preview shows them. */
export function iconLayout(width: number, count: number): { diameter: number; gap: number; margin: number; x: number; y: number } {
  const diameter = Math.max(24, Math.round(width * 0.07))
  const gap = Math.round(diameter / 4)
  const margin = Math.round(width * 0.02)
  const row = count * diameter + Math.max(0, count - 1) * gap

  return { diameter, gap, margin, x: width - margin - row, y: margin }
}
