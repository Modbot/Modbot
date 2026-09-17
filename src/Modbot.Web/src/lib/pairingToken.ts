/**
 * The pairing token: what this page hands the companion.
 *
 * It is base64url of `{"server": "<this server's origin>", "code": "<one-time pairing code>"}`.
 * The client decodes it, checks the address, and trades the code for its own device token at
 * `POST /api/v1/companion/pair` -- the same exchange as before, with the browser doing the typing.
 *
 * It carries the short-lived, single-use code and never anything longer-lived. The token travels
 * inside a `modbot-companion://` link, and links end up in browser history, in shell logs and in
 * Windows' record of protocol launches; a five-minute code found there a week later is worthless,
 * a device token would not be.
 *
 * `server` is the browser's own origin rather than something the server guessed. The address a
 * moderator reached this page at is, by construction, an address that reaches this server -- a
 * server behind a proxy frequently does not know its own public name.
 */

export const CLIENT_LINK_PREFIX = 'modbot-companion://pair?token='

function base64url(text: string): string {
  const bytes = new TextEncoder().encode(text)
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export function encodePairingToken(server: string, code: string): string {
  return base64url(JSON.stringify({ server, code }))
}

export function pairingLink(token: string): string {
  return CLIENT_LINK_PREFIX + token
}
