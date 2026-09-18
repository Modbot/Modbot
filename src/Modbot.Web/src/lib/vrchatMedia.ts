/**
 * Where to load a VRChat picture from.
 *
 * VRChat's image hosts refuse a browser that hotlinks them, so a profile picture, a world
 * thumbnail or a group banner drawn straight from `*.vrchat.cloud` comes back blocked. The server
 * fetches them on Modbot's behalf at `/api/files/vrchat?url=…` and this turns the stored URL into
 * that. Any other URL -- a Discord avatar, a Modbot-hosted file, a relative path -- is left as it
 * is, so every `<img>` can go through here without knowing where its picture lives.
 */

const PROXY = '/api/files/vrchat?url='

/** A host VRChat serves pictures from: `vrchat.cloud` itself or anything under it. */
function isVRChatHost(host: string): boolean {
  const h = host.toLowerCase()
  return h === 'vrchat.cloud' || h.endsWith('.vrchat.cloud')
}

export function vrchatMedia(url: string): string
export function vrchatMedia(url: string | null | undefined): string | null
export function vrchatMedia(url: string | null | undefined): string | null {
  if (!url) return null

  let parsed: URL
  try {
    parsed = new URL(url)
  } catch {
    // Not an absolute URL, so not one of VRChat's.
    return url
  }

  return isVRChatHost(parsed.hostname) ? PROXY + encodeURIComponent(url) : url
}
