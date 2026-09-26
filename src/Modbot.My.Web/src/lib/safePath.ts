/**
 * The path `/go?redir=` may send a browser to on a server, or null when it is not safe.
 *
 * This is the security boundary of `/go`. `redir` arrives from a URL anyone can build and put in a
 * link, and it is about to be appended to a host the person trusts. `//evil.example` would produce
 * `https://server//evil.example`, which a browser treats as protocol-relative and follows off
 * the server: an open redirect wearing the server's name.
 *
 * So the path must start with exactly one slash, and carry no backslash, no control character and
 * no URL scheme. A missing `redir` means the server's home page.
 *
 * Ported unchanged from the hand-written page this app replaced.
 */
export function safePath(raw: string | null | undefined): string | null {
  if (!raw) return '/'

  const path = String(raw)
  if (!path.startsWith('/')) return null
  if (path.startsWith('//')) return null
  if (path.includes('\\')) return null

  for (let i = 0; i < path.length; i++) {
    const code = path.charCodeAt(i)
    if (code < 0x20 || code === 0x7f) return null
  }

  if (/^\/+[a-z][a-z0-9+.-]*:/i.test(path)) return null

  return path
}
