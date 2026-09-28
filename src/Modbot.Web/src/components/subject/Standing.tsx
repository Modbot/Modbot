import { useCallback } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ModerationActions } from '@/components/moderation/ModerationActions'
import { api, type CurrentUser, type MembershipView, type PersonView } from '@/lib/api'
import { moderationApi } from '@/lib/autoMod'
import { formatDay, pastActions } from '@/lib/format'
import { can } from '@/lib/permissions'
import { useLoad } from '@/lib/useLoad'
import { cn } from '@/lib/utils'

/**
 * The answer to "have we dealt with them before", in one row under the popup's title.
 *
 * Every part of it was already in the popup: the ban line at the foot of Membership, the counts in
 * Past actions, the notes and case files each in a tab. A moderator reading a report on a phone had
 * to scroll past the whole profile to find any of it, and flags were not in the popup at all
 * (UX review 2026-09-25, finding 1). The row gathers them and each chip opens the tab that holds
 * the rest; the tabs and cards keep their detail.
 *
 * Only what applies is drawn. A read that failed says so rather than leaving its chip out, because
 * a missing chip reads as "none", and "none" is the one answer that must never be given by mistake.
 */

/** Which tab a chip opens. The popup maps these onto its own tabs. */
export type StandingTab = 'overview' | 'notes' | 'cases' | 'flags'

export function StandingBar({
  person,
  me,
  membership,
  version,
  notesId,
  notesPlatform,
  onOpen,
}: {
  person: PersonView
  me: CurrentUser
  /** Read once by the popup and shared with the Membership card and the phone's action row. */
  membership: { data: MembershipView | null; error: string | null } | null
  /** Changes after a kick, ban or unban, or a live fact about the person, and reads everything again. */
  version: number
  notesId: string | null
  notesPlatform: string
  onOpen: (tab: StandingTab) => void
}) {
  const vrchatId = person.vrChat?.id ?? null
  const discordId = person.discord?.id ?? null

  const seesProfile = can(me, 'ViewProfile')
  const readsNotes = can(me, 'ViewAuditLog')

  const loadHistory = useCallback(() => api.subjectHistory(vrchatId!), [vrchatId])
  const history = useLoad(seesProfile && vrchatId ? loadHistory : null, version)

  const loadNotes = useCallback(
    () => api.notes({ userId: notesId!, platform: notesPlatform, limit: 1 }),
    [notesId, notesPlatform],
  )
  const notes = useLoad(readsNotes && notesId ? loadNotes : null, version)

  const loadCases = useCallback(() => api.cases({ userId: vrchatId!, limit: 1 }), [vrchatId])
  const cases = useLoad(seesProfile && vrchatId ? loadCases : null, version)

  const loadFlags = useCallback(
    () => moderationApi.personFlags('open', { vrchat: vrchatId, discord: discordId }),
    [vrchatId, discordId],
  )
  const flags = useLoad(seesProfile && (vrchatId || discordId) ? loadFlags : null, version)

  const view = membership?.data ?? null
  const counts = history.data?.counts ?? null

  const chips: React.ReactNode[] = []
  const failed: string[] = []

  if (view?.banned) {
    chips.push(
      <Chip key="banned" tone="bad" onClick={() => onOpen('cases')}>
        Banned{view.bannedAt ? <> since <span className="font-mono">{formatDay(view.bannedAt)}</span></> : ''}
      </Chip>,
    )
  } else if (view?.banLiftedAt) {
    chips.push(
      <Chip key="lifted" onClick={() => onOpen('cases')}>
        Ban lifted <span className="font-mono">{formatDay(view.banLiftedAt)}</span>
      </Chip>,
    )
  }

  if (counts) {
    chips.push(
      <Chip key="repeat" tone={counts.status === 'repeat' ? 'bad' : undefined} onClick={() => onOpen('overview')}>
        {counts.status === 'repeat' ? 'Repeat · ' : ''}
        {pastActions(counts.actions, counts.moderators)}
      </Chip>,
    )
  }

  if (notes.data && notes.data.standing > 0) {
    chips.push(
      <Chip key="notes" onClick={() => onOpen('notes')}>
        {notes.data.standing} {notes.data.standing === 1 ? 'note' : 'notes'}
      </Chip>,
    )
  }

  if (flags.data && flags.data.open > 0) {
    chips.push(
      <Chip key="flags" onClick={() => onOpen('flags')}>
        {flags.data.open} open {flags.data.open === 1 ? 'flag' : 'flags'}
      </Chip>,
    )
  }

  if (cases.data && cases.data.total > 0) {
    chips.push(
      <Chip key="cases" onClick={() => onOpen('cases')}>
        Case files · {cases.data.total}
      </Chip>,
    )
  }

  if (membership?.error) failed.push('membership')
  if (history.error) failed.push('past actions')
  if (notes.error) failed.push('notes')
  if (flags.error) failed.push('flags')
  if (cases.error) failed.push('case files')

  // "No history" only once every read this account may make has answered, and answered cleanly.
  // Until then the row stays as it is, so a slow read never shows as a clean record.
  const waiting =
    (membership !== null && !membership.data && !membership.error)
    || (seesProfile && vrchatId !== null && !history.data && !history.error)
    || (readsNotes && notesId !== null && !notes.data && !notes.error)
    || (seesProfile && (vrchatId !== null || discordId !== null) && !flags.data && !flags.error)
    || (seesProfile && vrchatId !== null && !cases.data && !cases.error)

  return (
    <div
      className="flex min-h-(--row-h) flex-wrap items-center gap-1.5 border-b border-b-(length:--hairline) px-(--panel-pad) py-2"
      style={{ fontSize: 'var(--text-small)' }}
    >
      {chips}

      {chips.length === 0 && failed.length === 0 && !waiting && (
        <span className="text-muted-foreground">No history</span>
      )}

      {failed.length > 0 && (
        <span className="text-destructive">Could not load {failed.join(', ')}.</span>
      )}

      {view && !view.banned && view.members.firstSweepComplete && (
        <span className="text-muted-foreground">
          {chips.length > 0 || failed.length > 0 || !waiting ? '· ' : ''}
          {view.isMember ? 'Member' : 'Not a member'}
        </span>
      )}
    </div>
  )
}

function Chip({
  tone,
  onClick,
  children,
}: {
  tone?: 'bad'
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <Badge
      asChild
      variant={tone === 'bad' ? 'destructive' : 'outline'}
      className={cn('cursor-pointer', tone !== 'bad' && 'text-foreground hover:bg-muted')}
    >
      <button type="button" onClick={onClick}>
        {children}
      </button>
    </Badge>
  )
}

/**
 * Note, Kick and Ban, pinned to the foot of the popup on a phone.
 *
 * On a phone the popup is one long column, and these sat at the end of the profile, the Discord
 * card and the account card, so the moderator standing in an instance had to scroll to reach the
 * one thing they came to do. Here they never scroll away. On a wider screen the Membership card
 * keeps them and this row is not drawn.
 */
export function PhoneActions({
  me,
  vrchatId,
  name,
  membership,
  canNote,
  onNote,
  onActed,
}: {
  me: CurrentUser
  vrchatId: string | null
  name: string | null
  membership: MembershipView | null
  canNote: boolean
  onNote: () => void
  onActed: () => void
}) {
  const acts = vrchatId !== null && membership !== null
  if (!acts && !canNote) return null

  return (
    <div className="flex items-center justify-end gap-1.5 border-t border-t-(length:--hairline) bg-strip px-(--panel-pad) py-2 pb-[max(0.5rem,env(safe-area-inset-bottom))]">
      {canNote && (
        <Button size="sm" variant="outline" onClick={onNote} className="mr-auto">
          Note
        </Button>
      )}
      {acts && (
        <ModerationActions
          me={me}
          person={{ userId: vrchatId, banned: membership.banned, isMember: membership.isMember }}
          name={name ?? vrchatId}
          onDone={onActed}
        />
      )}
    </div>
  )
}
