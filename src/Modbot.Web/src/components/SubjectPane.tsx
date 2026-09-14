import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { api, ApiError, type AuditEntry, type CurrentUser, type MembershipView } from '@/lib/api'
import { FactTime, SourceBadge } from '@/components/facts'
import { SubjectCaseFiles } from '@/components/SubjectCaseFiles'
import { SubjectHistory } from '@/components/SubjectHistory'
import { UserProfileCard } from '@/components/UserProfileCard'
import { ago, formatDay } from '@/lib/format'
import { can } from '@/lib/permissions'
import { X } from 'lucide-react'

/**
 * Everything Modbot knows about one person, over the current view.
 *
 * Spec 10.2: a pane rather than a page, because moderation is interruption-driven. Somebody
 * scanning the audit log for one thing notices a name and wants to know about it *without losing
 * the scan* — a separate page costs the scroll position and the filters, so in practice people
 * don't check, and the history Modbot collected goes unread at the moment it mattered.
 *
 * Three things are here: the person's stored VRChat profile, with the age of every field written
 * beside it and the sticky 18+ flag (user profile sync design); their membership and ban standing
 * as the sweeps last read them (member and ban sync design); and every fact recorded about them.
 * Presence analytics and a Discord link are still absent -- there is no Discord bot -- and the
 * pane says so rather than inventing them.
 */
export function SubjectPane({
  subjectId,
  me,
  onClose,
}: {
  subjectId: string
  /** The signed-in account, so the profile card knows which controls to draw. */
  me: CurrentUser
  onClose: () => void
}) {
  const [entries, setEntries] = useState<AuditEntry[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Mounted fresh per subject (App keys it on the id), so there is nothing to reset here and no
  // window in which the previous person's history is shown under this one's name.
  useEffect(() => {
    let cancelled = false

    api
      .audit({ subject: subjectId, limit: 50 })
      .then((page) => {
        if (!cancelled) setEntries(page.entries)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to read this history.'
            : 'Could not load this person’s history.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [subjectId])

  // Escape closes, because the pane is opened speculatively and dismissing it should cost nothing.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="fixed inset-0 z-40 flex justify-end">
      <button
        type="button"
        aria-label="Close"
        className="flex-1 bg-black/30"
        onClick={onClose}
      />
      <aside
        className="flex w-full max-w-lg flex-col overflow-auto border-l bg-card shadow-xl"
        style={{ borderLeftWidth: 'var(--hairline)' }}
      >
        <header
          className="sticky top-0 flex items-start gap-3 border-b bg-card px-4 py-3"
          style={{ borderBottomWidth: 'var(--hairline)' }}
        >
          <div className="min-w-0 flex-1">
            <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              Profile and recorded history
            </div>
            {/* The id verbatim and unparsed: VRChat ids are opaque, and a legacy one looks
                nothing like a modern one (spec 3.1.1). */}
            <div className="truncate font-mono font-medium" title={subjectId}>
              {subjectId}
            </div>
          </div>
          <Button variant="ghost" size="sm" onClick={onClose} title="Close">
            <X className="size-4" />
          </Button>
        </header>

        <div className="flex flex-col gap-3 p-4">
          <UserProfileCard subjectId={subjectId} me={me} />

          <SubjectHistory subjectId={subjectId} />
          {can(me, 'ViewProfile') && <SubjectCaseFiles subjectId={subjectId} />}
          {can(me, 'ViewMembers') && <MembershipCard subjectId={subjectId} />}

          <div className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
            Recorded history
          </div>
          <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Time in world is not synced yet. What follows is every fact recorded about this
            person, newest first.
          </p>

          {error && (
            <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
              {error}
            </p>
          )}

          {!error && entries === null && (
            <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              Loading…
            </p>
          )}

          {entries?.length === 0 && (
            <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              Nothing recorded about this person in the history Modbot holds.
            </p>
          )}

          {entries && entries.length > 0 && (
            <ol className="flex flex-col gap-2">
              {entries.map((entry) => (
                <li
                  key={entry.id}
                  className="rounded-md border px-3 py-2"
                  style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
                >
                  <div className="flex items-center gap-2">
                    <SourceBadge source={entry.source} />
                    <span className="font-medium">{entry.type}</span>
                    <span className="flex-1" />
                    <FactTime entry={entry} />
                  </div>
                  {entry.description && (
                    <div className="mt-1 text-muted-foreground">{entry.description}</div>
                  )}
                  {entry.actorId && (
                    <div className="mt-1 text-muted-foreground">
                      by {entry.actorName ?? <span className="font-mono">{entry.actorId}</span>}
                    </div>
                  )}
                </li>
              ))}
            </ol>
          )}
        </div>
      </aside>
    </div>
  )
}

/**
 * Membership and ban standing, as the sweeps last read them, with how old that reading is.
 *
 * "Not a member" from a list synced an hour ago and "not a member" from a list still being read
 * for the first time are different claims, so the age travels with the answer.
 */
function MembershipCard({ subjectId }: { subjectId: string }) {
  const [view, setView] = useState<MembershipView | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    api
      .membership(subjectId)
      .then((next) => {
        if (!cancelled) setView(next)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to view membership.'
            : 'Could not load membership.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [subjectId])

  return (
    <div
      className="rounded-md border px-3 py-2"
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      <div className="font-medium">Membership</div>

      {error && <p className="mt-1 text-destructive">{error}</p>}
      {!error && !view && <p className="mt-1 text-muted-foreground">Loading…</p>}

      {view && (
        <div className="mt-1 flex flex-col gap-1.5">
          {!view.members.firstSweepComplete ? (
            <p className="text-warn">
              The member list is still being read for the first time, so whether this person is a
              member is not known yet.
            </p>
          ) : view.isMember ? (
            <p>
              Member{view.joinedAt ? <> since {formatDay(view.joinedAt)}</> : ''}
              {view.isRepresenting ? ', representing the group' : ''}.
            </p>
          ) : view.known ? (
            <p>
              Not a member{view.leftAt ? <> — no longer listed as of {formatDay(view.leftAt)}</> : ''}
              {view.joinedAt ? <>, had joined {formatDay(view.joinedAt)}</> : ''}.
            </p>
          ) : (
            <p className="text-muted-foreground">Not a member as of the last sweep.</p>
          )}

          {view.roleNames.length > 0 && (
            <div className="flex flex-wrap items-center gap-1">
              <span className="text-muted-foreground">Roles:</span>
              {view.roleNames.map((name, i) => (
                <Badge key={view.roleIds[i] ?? name} variant="secondary" title={view.roleIds[i]}>
                  {name}
                </Badge>
              ))}
            </div>
          )}

          {view.managerNotes && (
            <p className="text-muted-foreground">
              Manager notes: <span className="whitespace-pre-wrap break-words text-foreground">{view.managerNotes}</span>
            </p>
          )}

          {view.banned ? (
            <p className="text-destructive">
              On the ban list{view.bannedAt ? <> since {formatDay(view.bannedAt)}</> : ''}.
            </p>
          ) : view.banLiftedAt ? (
            <p className="text-muted-foreground">
              Was banned{view.bannedAt ? <> on {formatDay(view.bannedAt)}</> : ''}; the ban was lifted by {formatDay(view.banLiftedAt)}.
            </p>
          ) : !view.bans.firstSweepComplete ? (
            <p className="text-muted-foreground">The ban list is still being read for the first time.</p>
          ) : null}

          <p className="text-muted-foreground">
            Member list synced {ago(view.members.lastSyncedAt, view.members.now)}; ban list synced{' '}
            {ago(view.bans.lastSyncedAt, view.bans.now)}.
          </p>
        </div>
      )}
    </div>
  )
}
