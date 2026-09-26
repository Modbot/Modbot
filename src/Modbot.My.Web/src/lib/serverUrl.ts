export const MAX_SERVER_URL_LENGTH = 2048

/**
 * The origin of a Modbot server's address, or null when it is not one.
 *
 * The same rule as my.modbot.co's own `Common/ServerUrl.cs`, so an address the browser saves is one
 * my.modbot.co would pass on in the same shape: absolute `https` only, no username or password, and
 * only the origin kept (`https://modbot.example/settings?x=1` becomes `https://modbot.example`).
 *
 * An `http` URL is refused rather than warned about: stored, it would downgrade every redirect
 * `/go` makes to it afterwards.
 */
export function normaliseServerUrl(raw: string | null | undefined): string | null {
  if (!raw) return null

  const text = raw.trim()
  if (!text || text.length > MAX_SERVER_URL_LENGTH) return null

  let url: URL
  try {
    url = new URL(text)
  } catch {
    return null
  }

  if (url.protocol !== 'https:') return null
  if (url.username || url.password) return null

  return url.origin
}
