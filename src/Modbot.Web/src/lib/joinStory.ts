import type { AuditEntry } from './api.ts'

/**
 * How somebody came into the group, read from the facts about them: who invited them, who let
 * them in, and how often they asked and were turned down.
 *
 * Pure, so a test can check it without a browser. The Membership card reads the facts and draws
 * what this returns.
 */

/** Somebody named on a fact, as the sentence on screen links them. */
export type Who = { platform: string | null; id: string | null; name: string | null }

export type JoinStory = {
  /** Who sent the newest invite: a moderator in VRChat, or Modbot's auto-invite (no id). */
  invitedBy: Who | null
  /** Who let them in from a join request, where a fact says so. */
  approvedBy: Who | null
  /** How many join requests VRChat recorded. */
  asked: number
  /** How many of those were turned down, and by whom, each name once, newest first. */
  rejected: { count: number; by: Who[] }
  /** How many times they were blocked from asking again. */
  blocked: number
}

/** The fact types the story is made from, to ask the log for exactly these. */
export const JOIN_STORY_TYPES = [
  'vrchat.group.invite.create',
  'modbot.group.auto-invite',
  'vrchat.group.member.join',
  'vrchat.group.request.create',
  'vrchat.group.request.reject',
  'vrchat.group.request.block',
  'modbot.action.request.approve',
  'modbot.action.request.reject',
]

const INVITES = new Set(['vrchat.group.invite.create', 'modbot.group.auto-invite'])

/**
 * One decision is one group: a fact and the facts linked to it. Modbot's own record of a request
 * turned down from its Requests page and VRChat's record of the same refusal are one refusal, not
 * two, and the moderator who pressed the button is the one to name rather than the bot account
 * VRChat saw.
 */
function decisions(entries: AuditEntry[]): AuditEntry[][] {
  return entries.map((entry) => [entry, ...(entry.linked ?? [])])
}

function whoDid(entry: AuditEntry): Who {
  return { platform: entry.actorPlatform, id: entry.actorId, name: entry.actorName }
}

/** The newest first, as the log sends them, but not trusting that it always will. */
function newestFirst(a: AuditEntry, b: AuditEntry): number {
  return b.occurredAt.localeCompare(a.occurredAt) || b.id - a.id
}

export function joinStory(entries: AuditEntry[], subjectId: string): JoinStory {
  const groups = decisions([...entries].sort(newestFirst))

  let invitedBy: Who | null = null
  let approvedBy: Who | null = null
  let joinedBy: Who | null = null
  let asked = 0
  let rejectedCount = 0
  let blocked = 0
  const rejecters: Who[] = []

  for (const group of groups) {
    const of = (type: string) => group.find((e) => e.type === type)

    const invite = group.find((e) => INVITES.has(e.type))
    if (invite && !invitedBy) {
      // Modbot's auto-invite names no actor: nobody pressed anything at that moment.
      invitedBy = invite.actorId ? whoDid(invite) : { platform: 'Modbot', id: null, name: 'Modbot' }
    }

    const approved = of('modbot.action.request.approve')
    if (!approvedBy && approved?.actorId) approvedBy = whoDid(approved)

    // VRChat's own join entry names who added them when somebody did: "added to the group by".
    // Joining on one's own invite names nobody else, so only an actor who is somebody else counts,
    // and only for somebody who asked to join (see below).
    const join = of('vrchat.group.member.join')
    if (!joinedBy && join?.actorId && join.actorId !== subjectId) joinedBy = whoDid(join)

    if (of('vrchat.group.request.create')) asked++
    if (of('vrchat.group.request.block')) blocked++

    const rejectedHere = of('modbot.action.request.reject') ?? of('vrchat.group.request.reject')
    if (rejectedHere) {
      rejectedCount++
      if (rejectedHere.actorId && !rejecters.some((r) => r.id === rejectedHere.actorId && r.platform === rejectedHere.actorPlatform))
        rejecters.push(whoDid(rejectedHere))
    }
  }

  // VRChat's join entry does not say whether the person was let in from a request or came in on an
  // invite, so the somebody it names is only taken as the one who let them in when the person had
  // asked and nobody invited them. With an invite on record, that is the line to read instead.
  if (!approvedBy && joinedBy && asked > 0 && !invitedBy) approvedBy = joinedBy

  return { invitedBy, approvedBy, asked, rejected: { count: rejectedCount, by: rejecters }, blocked }
}

/** Whether there is anything to say. */
export function hasStory(story: JoinStory): boolean {
  return (
    story.invitedBy !== null || story.approvedBy !== null || story.asked > 0 || story.rejected.count > 0 || story.blocked > 0
  )
}

/** "once", "twice", "3 times". */
export function times(n: number): string {
  if (n === 1) return 'once'
  if (n === 2) return 'twice'
  return `${n} times`
}
