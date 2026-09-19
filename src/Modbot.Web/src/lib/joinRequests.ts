// Relative, with the extension, rather than the '@/' alias the rest of the app uses: the Node test
// runner resolves neither the alias nor an extensionless path, and this is the piece of the screen
// worth testing. `permissions.ts` imports nothing at run time, so it loads as it is too.
import type { CurrentUser, JoinRequestAnswer, JoinRequestRow, ModerationActionResult } from './api.ts'
import { can } from './permissions.ts'

/** Whether this person may answer a join request at all. */
export function mayAnswer(me: CurrentUser | null): boolean {
  return can(me, 'AnswerJoinRequests')
}

/** The sentence that names what is about to happen, for the confirmation. */
export function confirmTitle(answer: JoinRequestAnswer, name: string): string {
  return answer === 'approve' ? `Let ${name} into the group?` : `Turn down ${name}'s request?`
}

/**
 * What the result line says once VRChat has answered.
 *
 * A request that is no longer there is its own outcome, said in plain words. It is the ordinary
 * ending for a queue two moderators are working at once, and calling it a failure would train
 * people to ignore the one line on the screen that reports real failures.
 */
export function resultText(
  answer: JoinRequestAnswer,
  result: Pick<ModerationActionResult, 'done' | 'error' | 'rateLimited' | 'repeat' | 'gone'>,
): string {
  if (result.done) {
    const past = answer === 'approve' ? 'Let in.' : 'Turned down.'
    return result.repeat ? `${past} Already sent.` : past
  }

  if (result.gone) return result.error ?? 'That request is no longer waiting.'

  if (result.rateLimited) return `VRChat rate limited this. Nothing happened. ${result.error ?? ''}`.trim()

  return result.error ?? 'VRChat refused it and did not say why.'
}

/**
 * Whether the row should leave the list once VRChat has answered.
 *
 * Both a done answer and a request that turned out to be gone leave, because in both cases the
 * person is no longer waiting. A refusal keeps the row: nothing happened, and it is still there.
 */
export function rowIsAnswered(
  result: Pick<ModerationActionResult, 'done' | 'gone'>,
): boolean {
  return result.done || result.gone
}

/**
 * What the row says about this person's history with the group, or null when there is nothing to
 * say.
 *
 * One line, not three. A moderator reading a queue is scanning for the rows that need thought, and
 * the only thing that earns a mark beside a name is that the group has dealt with this person
 * before.
 */
export function historyNote(row: JoinRequestRow): string | null {
  if (row.banned) return 'Banned'
  if (row.bannedBefore) return 'Banned before'
  if (row.wasMember) return 'Was a member'
  return null
}
