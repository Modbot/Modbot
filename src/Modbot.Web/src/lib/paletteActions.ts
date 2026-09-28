// Relative, with the extension, rather than the '@/' alias the rest of the app uses: the Node test
// runner resolves neither the alias nor an extensionless path, and which words start an action and
// who is offered one is worth a test. Its imports load in Node as they are.
import type { CurrentUser, ModerationActionName } from './api.ts'
import { actionsFor, type PersonStanding } from './moderationActions.ts'
import { can } from './permissions.ts'

/** A word the palette acts on when a name follows it: "ban fenya". */
export type PaletteVerb = ModerationActionName | 'note'

const VERBS: readonly PaletteVerb[] = ['ban', 'kick', 'unban', 'note']

/**
 * What was typed as a verb and the name after it, or null when it does not start with one of the
 * palette's verbs or no name follows.
 *
 * Finding somebody and banning them is the job a moderator does under pressure, and the palette
 * had them search, open the popup, press Ban and only then reach the confirmation (UX review
 * 2026-09-27, idea 2). The name needs two characters, the least the server searches on.
 */
export function splitVerb(typed: string): { verb: PaletteVerb; name: string } | null {
  const match = /^(\S+)\s+(.+)$/.exec(typed.trim())
  if (!match) return null

  const verb = match[1].toLowerCase() as PaletteVerb
  if (!VERBS.includes(verb)) return null

  const name = match[2].trim()
  return name.length >= 2 ? { verb, name } : null
}

/**
 * Whether the palette offers this moderator the verb on this person.
 *
 * Kick, ban and unban by the rule the popup's buttons follow (`actionsFor`), so the palette never
 * offers what the popup would not: an unban on somebody who is not banned, a kick on somebody who
 * left. A note needs the permission that writes one and the one that opens the Notes tab it lands on.
 */
export function mayDo(me: CurrentUser | null, verb: PaletteVerb, person: PersonStanding): boolean {
  if (!person.userId) return false
  if (verb === 'note') return can(me, 'WriteNotes') && can(me, 'ViewAuditLog')
  return actionsFor(me, person).some((o) => o.action === verb)
}

/** The row's words. The dots say a confirmation or a form comes next, not the action itself. */
export function verbLabel(verb: PaletteVerb, name: string): string {
  switch (verb) {
    case 'ban':
      return `Ban ${name}…`
    case 'kick':
      return `Kick ${name}…`
    case 'unban':
      return `Unban ${name}…`
    case 'note':
      return `Add a note to ${name}…`
  }
}
