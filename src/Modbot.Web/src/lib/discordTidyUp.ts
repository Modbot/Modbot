// Relative, with the extension, so the Node test runner can load this file as it is (see nav.ts).
import type { QuietChannelRow, RoleFlag } from './api.ts'
import { howLong } from './format.ts'

/**
 * The words the Discord page's Roles and Channels tabs put on screen (Discord tidy-up design). The
 * server decides what is marked and in which order; this only names it.
 */

/** Each mark the roles report puts on a role, as the page writes it. */
export const ROLE_FLAG_LABELS: Record<RoleFlag, string> = {
  'no-members': 'No members',
  'same-name': 'Same name as another role',
  'same-permissions-and-colour': 'Same permissions and colour as another role',
  'bot-role': 'Bot role',
}

/**
 * How long a channel has been quiet, against the server's clock, in Modbot's units with no weeks:
 * "5h", "3mth", "2y". When no last message is known it says why instead: the bot cannot read the
 * channel, its history is still being read back, or nobody has ever written in it.
 */
export function quietFor(channel: QuietChannelRow, now: string): string {
  if (!channel.canRead) return "Can't read"
  if (channel.lastMessageAt) return howLong(channel.lastMessageAt, now)
  return channel.stillReading ? 'Still reading' : 'No messages'
}
