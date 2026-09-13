import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type AuditEntry } from '@/lib/api'
import { FactTime, SourceBadge } from '@/components/facts'
import { X } from 'lucide-react'

/**
 * Everything Modbot knows about one person, over the current view.
 *
 * Spec 10.2: a pane rather than a page, because moderation is interruption-driven. Somebody
 * scanning the audit log for one thing notices a name and wants to know about it *without losing
 * the scan* — a separate page costs the scroll position and the filters, so in practice people
 * don't check, and the history Modbot collected goes unread at the moment it mattered.
 *
 * <strong>What is here is the fact log and nothing else.</strong> The spec's pane also carries
 * identity, roles, membership, presence analytics and a Discord link. None of those have a
 * producer yet: there is no member sync, no profile fetch, and no Discord bot. Rendering a name
 * and a join date from nowhere is exactly the failure the Members screen was emptied to avoid, so
 * the pane shows the person's recorded history and says plainly what it is missing.
 */
export function SubjectPane({
  subjectId,
  onClose,
}: {
  subjectId: string
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
              Recorded history
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
          <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Modbot has no display name, join date, roles or time-in-world for this person — none of
            those are synced yet. What follows is every fact recorded about them, newest first.
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
