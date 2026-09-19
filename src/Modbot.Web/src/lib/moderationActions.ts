// Relative, with the extension, rather than the '@/' alias the rest of the app uses: the Node test
// runner resolves neither the alias nor an extensionless path, and this is the piece of the feature
// worth testing. `permissions.ts` imports nothing at run time, so it loads as it is too.
import type { CurrentUser, ModerationActionName } from './api.ts'
import { can } from './permissions.ts'

/** What Modbot knows about where this person stands, as little as a table row may know. */
export type PersonStanding = {
  /** VRChat's id. Empty or missing means there is nobody to act on. */
  userId?: string | null
  /** True when the ban list holds them now. */
  banned?: boolean | null
  /** True when the member list holds them now. */
  isMember?: boolean | null
}

/** One offered action: the word on the button, and whether it is the drastic one. */
export type OfferedAction = {
  action: ModerationActionName
  label: string
  destructive: boolean
}

const LABELS: Record<ModerationActionName, string> = {
  kick: 'Kick',
  ban: 'Ban',
  unban: 'Unban',
}

/**
 * Which of kick, ban and unban to put in front of this person for this moderator.
 *
 * Separate from the components that draw it because three places offer these — the person popup,
 * the Members row and the Bans page — and three copies of "is this one banned, may this one ban"
 * is three chances to offer an unban on somebody who is not banned.
 *
 * Someone already banned is offered **Unban instead of Ban**, not both: the two are opposite
 * answers to one question, and a row that offers both is asking the moderator to notice which one
 * matches a state they cannot see from the button.
 *
 * Kick is left out for somebody who is not a member, because there is nothing to remove them from.
 * It stays for anyone whose membership Modbot has not read, since "not listed yet" is not the same
 * claim as "not there".
 *
 * **Ban is offered to everybody**, member or not. VRChat's group ban takes a user id rather than a
 * membership, and keeping somebody out before they ever arrive — heard about from another group,
 * from Discord, from a flag — is an ordinary thing for a moderator to want.
 */
export function actionsFor(me: CurrentUser | null, person: PersonStanding): OfferedAction[] {
  if (!person.userId) return []

  const offered: OfferedAction[] = []

  if (person.banned === true) {
    if (can(me, 'Unban')) offered.push(at('unban'))
    return offered
  }

  if (person.isMember !== false && can(me, 'Kick')) offered.push(at('kick'))
  if (can(me, 'Ban')) offered.push(at('ban'))

  return offered
}

/** Whether a reason has to be picked before this action can be sent. */
export function reasonRequired(action: ModerationActionName, groupRequiresOne: boolean): boolean {
  return action === 'ban' || groupRequiresOne
}

/**
 * The sentence that names what is about to happen, for the confirmation.
 *
 * "Ban X from the group?" asks about removing somebody, which is the wrong question for a person
 * who was never in it: they are being kept out, not taken out. So a ban on somebody Modbot's
 * member list says is not there asks "Ban X?" instead. Anyone whose membership Modbot has not read
 * keeps the longer sentence, for the reason `actionsFor` keeps their kick.
 */
export function confirmTitle(
  action: ModerationActionName,
  name: string,
  isMember?: boolean | null,
): string {
  switch (action) {
    case 'kick':
      return `Kick ${name} from the group?`
    case 'ban':
      return isMember === false ? `Ban ${name}?` : `Ban ${name} from the group?`
    case 'unban':
      return `Unban ${name}?`
  }
}

/** What the result line says once VRChat has answered. */
export function resultText(
  action: ModerationActionName,
  result: { done: boolean; error: string | null; rateLimited: boolean; repeat: boolean },
): string {
  if (result.done) {
    const past: Record<ModerationActionName, string> = {
      kick: 'Kicked.',
      ban: 'Banned.',
      unban: 'Unbanned.',
    }
    return result.repeat ? `${past[action]} Already sent.` : past[action]
  }

  if (result.rateLimited) return `VRChat rate limited this. Nothing happened. ${result.error ?? ''}`.trim()

  return result.error ?? 'VRChat refused it and did not say why.'
}

function at(action: ModerationActionName): OfferedAction {
  return { action, label: LABELS[action], destructive: action !== 'unban' }
}
