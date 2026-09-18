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
 * The operator's switch, read from the status the app loads before it draws anything.
 *
 * On until told otherwise: a picture drawn before the first status arrives is far likelier to be
 * on a server with the switch left alone than on one that turned it off.
 */
let proxied = true

/** Called once the server's onboarding status has been read. */
export function setVRChatImagesProxied(on: boolean | undefined): void {
  if (on !== undefined) proxied = on
}

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

  if (!proxied) return url

  return isVRChatHost(parsed.hostname) ? PROXY + encodeURIComponent(url) : url
}
