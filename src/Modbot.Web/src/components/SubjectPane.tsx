import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type AuditEntry, type CurrentUser } from '@/lib/api'
import { FactTime, SourceBadge } from '@/components/facts'
import { UserProfileCard } from '@/components/UserProfileCard'
import { X } from 'lucide-react'

/**
 * Everything Modbot knows about one person, over the current view.
 *
 * Spec 10.2: a pane rather than a page, because moderation is interruption-driven. Somebody
 * scanning the audit log for one thing notices a name and wants to know about it *without losing
 * the scan* — a separate page costs the scroll position and the filters, so in practice people
 * don't check, and the history Modbot collected goes unread at the moment it mattered.
 *
 * Two things are here: the person's stored VRChat profile, with the age of every field written
 * beside it and the sticky 18+ flag (user profile sync design), and every fact recorded about
 * them. Roles, membership, presence analytics and a Discord link are still absent -- there is no
 * member sync and no Discord bot -- and the pane says so rather than inventing them.
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

          <div className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
            Recorded history
          </div>
          <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Roles and time in world are not synced yet. What follows is every fact recorded about
            this person, newest first.
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
