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

/**
 * Kept for the status call that still reports the operator's switch, and deliberately unused.
 *
 * Every picture goes to Modbot either way now. With the switch off Modbot answers with a redirect
 * to VRChat instead of fetching the picture itself, so one place decides and the browser does not
 * have to be told which server it is talking to before it can draw a face.
 */
export function setVRChatImagesProxied(_on: boolean | undefined): void {}

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
