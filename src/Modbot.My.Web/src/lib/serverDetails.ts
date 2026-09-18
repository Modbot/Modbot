/** A group, as the register page shows it. Every part may be missing. */
export type GroupDetails = {
  groupId: string | null
  name: string | null
  iconUrl: string | null
  bannerUrl: string | null
}

export const NO_GROUP: GroupDetails = { groupId: null, name: null, iconUrl: null, bannerUrl: null }

const MAX_NAME_LENGTH = 200

const MAX_ID_LENGTH = 128

const MAX_URL_LENGTH = 1024

/** How long to wait for a Modbot to say what it is. Past that, the page shows the address alone. */
const ASK_TIMEOUT_MS = 4_000

/** Control and format characters go: a right-to-left override makes one name render as another. */
function hidden(character: string): boolean {
  const code = character.codePointAt(0) ?? 0

  return (
    code < 0x20 ||
    code === 0x7f ||
    (code >= 0x200b && code <= 0x200f) ||
    (code >= 0x2028 && code <= 0x202e) ||
    (code >= 0x2066 && code <= 0x2069)
  )
}

function text(value: unknown, maxLength: number): string | null {
  if (typeof value !== 'string') return null

  const cleaned = [...value].filter((character) => !hidden(character)).join('').trim()

  return cleaned ? cleaned.slice(0, maxLength) : null
}

/** A picture, which VRChat serves over https. Anything else is left out. */
function picture(value: unknown): string | null {
  if (typeof value !== 'string' || !value.trim() || value.length > MAX_URL_LENGTH) return null

  try {
    const url = new URL(value.trim())
    return url.protocol === 'https:' ? url.href : null
  } catch {
    return null
  }
}

/**
 * The group a register link claims, for the first paint.
 *
 * **Hints, and nothing more.** Anybody can write a link, so these are shown and never saved, and
 * the page replaces them with what the Modbot itself says as soon as it answers
 * (register details spec 2.1).
 */
export function detailsFromLink(search: URLSearchParams): GroupDetails {
  return {
    groupId: text(search.get('groupId'), MAX_ID_LENGTH),
    name: text(search.get('name'), MAX_NAME_LENGTH),
    iconUrl: picture(search.get('icon')),
    bannerUrl: picture(search.get('banner')),
  }
}

/**
 * What a Modbot answered about itself, cleaned. Null when the answer was not one.
 *
 * `ownerEmail` is deliberately not read: it is the operator's address, it is no business of a page,
 * and the browser having it at all would be a way for it to end up somewhere it should not.
 */
export function detailsFromServer(value: unknown): GroupDetails | null {
  if (!value || typeof value !== 'object') return null

  const answer = value as Record<string, unknown>

  return {
    groupId: text(answer.groupId, MAX_ID_LENGTH),
    name: text(answer.name, MAX_NAME_LENGTH),
    iconUrl: picture(answer.iconUrl),
    bannerUrl: picture(answer.bannerUrl),
  }
}

/**
 * Asks a Modbot what it is. `GET <origin>/api/server` is open and allows this page to read it, so
 * the browser can check the link for itself while my.modbot.co checks it server-side before saving
 * anything.
 */
export async function askServer(origin: string, signal?: AbortSignal): Promise<GroupDetails | null> {
  try {
    const response = await fetch(`${origin}/api/server`, {
      // Nothing of this browser's goes to somebody else's server.
      credentials: 'omit',
      redirect: 'follow',
      signal: signal
        ? AbortSignal.any([signal, AbortSignal.timeout(ASK_TIMEOUT_MS)])
        : AbortSignal.timeout(ASK_TIMEOUT_MS),
    })

    if (!response.ok) return null

    return detailsFromServer(await response.json())
  } catch {
    return null
  }
}
