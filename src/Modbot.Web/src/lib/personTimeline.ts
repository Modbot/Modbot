import type { AuditEntry, PersonView } from './api.ts'
import type { LiveEvent } from './liveStream.ts'
import type { Subject } from './subject.ts'

/**
 * One person's timeline: every fact about any of their accounts or done by any of them, merged by
 * the server (`person` on GET /api/audit). The pure parts are here, for the popup's Overview and
 * Activity tabs to share.
 */

/** The account a person's popup was opened on, which is what the server ties the others to. */
export type PersonAsked = { platform: 'VRChat' | 'Discord' | 'Modbot'; id: string }

/**
 * The account the address named. The server ties the rest to it with the same rules and the same
 * permissions it used to answer the popup, so the timeline covers exactly the accounts on screen.
 */
export function askedOf(subject: Pick<Subject, 'kind' | 'id'>): PersonAsked {
  if (subject.kind === 'discord-person') return { platform: 'Discord', id: subject.id }
  if (subject.kind === 'account') return { platform: 'Modbot', id: subject.id }
  return { platform: 'VRChat', id: subject.id }
}

/** What each row was found under, as the popup's lists name it. */
const VRCHAT = 'VRChat'
const DISCORD = 'Discord'
const ACCOUNT = 'Modbot account'

type Accounts = Pick<PersonView, 'vrChat' | 'discord' | 'account'>

/**
 * Which of the person's accounts a fact was found under: the one it is about, or the one that did it.
 *
 * The merge is a convenience, not a claim: a Discord row is still a Discord row, and saying so on
 * the row is what stops a Discord ban being read as a group one (one view per person design §4).
 */
export function foundUnder(
  person: Accounts,
  entry: Pick<AuditEntry, 'subjectPlatform' | 'subjectId' | 'actorPlatform' | 'actorId'>,
): string | undefined {
  const is = (platform: string, id: string | null | undefined) =>
    !!id
    && ((entry.subjectPlatform === platform && entry.subjectId === id)
      || (entry.actorPlatform === platform && entry.actorId === id))

  if (is('VRChat', person.vrChat?.id)) return VRCHAT
  if (is('Discord', person.discord?.id)) return DISCORD
  if (is('Modbot', person.account?.id)) return ACCOUNT
  return undefined
}

/** Whether a live event is about any of the person's accounts, or done by any of them. */
export function concernsAny(person: Accounts, event: Pick<LiveEvent, 'subject' | 'actor'>): boolean {
  const accounts: [string, string | undefined][] = [
    ['VRChat', person.vrChat?.id],
    ['Discord', person.discord?.id],
    ['Modbot', person.account?.id],
  ]

  return accounts.some(
    ([platform, id]) =>
      !!id
      && ((event.subject.platform === platform && event.subject.id === id)
        || (event.actor !== null && event.actor.platform === platform && event.actor.id === id)),
  )
}
