/**
 * What a username may be made of, matching the server's rule exactly
 * (`UsernameRules` in Modbot.Core, username rules and deleting accounts design §2).
 *
 * Here so that a form says what is wrong while somebody is still typing, rather than after they
 * have pressed the button. The server is what enforces the rule; this only saves the round trip,
 * and a difference between the two would be a form that lets through something the server refuses.
 *
 * Checked against the trimmed name, which is what the server stores.
 */
const ALLOWED = /^[A-Za-z0-9_]+$/

export const MAX_USERNAME_LENGTH = 64

/** The problem with this username in plain words, or null when there is none. */
export function usernameProblem(typed: string): string | null {
  const trimmed = typed.trim()

  if (trimmed.length === 0) return 'A username is required.'
  if (trimmed.length > MAX_USERNAME_LENGTH) {
    return `That username is longer than ${MAX_USERNAME_LENGTH} characters.`
  }
  if (!ALLOWED.test(trimmed)) return 'A username can only have letters, numbers and underscores in it.'

  return null
}
