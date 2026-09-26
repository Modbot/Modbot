import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { EmptyRow } from '@/components/PanelGrid'
import { Panel } from '@/components/subject/shared'
import { api, ApiError, type CaseFileList } from '@/lib/api'
import { formatDay } from '@/lib/format'
import { go } from '@/lib/router'

/**
 * This person's case files, on their pane.
 *
 * The pane is where a moderator looks when a name catches their eye mid-scan (spec 10.2), and
 * "have we written down why we banned them before" is exactly the question that arises there.
 * Withdrawn ones are listed too and say so: a case file that was written and then withdrawn is a
 * different thing from one nobody ever wrote, and the pane must not blur the two.
 */
export function SubjectCaseFiles({ subjectId }: { subjectId: string }) {
  const [list, setList] = useState<CaseFileList | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    api
      .cases({ userId: subjectId, includeWithdrawn: true, limit: 20 })
      .then((next) => {
        if (!cancelled) setList(next)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see case files.'
            : 'Could not load this person’s case files.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [subjectId])

  // A failure says so: a missing section would read as "no case files", which is the one thing a
  // moderator must not be told by mistake.
  if (error) {
    return (
      <Panel title="Case files" flush>
        <EmptyRow tone="danger">{error}</EmptyRow>
      </Panel>
    )
  }
  if (!list) return null

  return (
    <Panel title="Case files" flush>
      {list.cases.length === 0 ? (
        <EmptyRow>No case files.</EmptyRow>
      ) : (
        <ul className="flex flex-col" style={{ fontSize: 'var(--text-small)' }}>
          {list.cases.map((file) => (
            <li
              key={file.id}
              className="flex min-h-(--row-h) flex-wrap items-center gap-x-2 gap-y-1 border-t border-t-(length:--hairline) px-(--panel-pad) py-1 first:border-t-0"
            >
              <button
                type="button"
                onClick={() => go(`/cases/${file.id}`)}
                className="rounded-sm text-left font-medium hover:underline focus-visible:outline-2 focus-visible:outline-ring"
              >
                {file.reasons.map((r) => r.label).join(', ') || 'No reason recorded'}
              </button>
              {file.withdrawn && <Badge variant="secondary">withdrawn</Badge>}
              {file.evidenceCount > 0 && (
                <span className="text-muted-foreground">
                  {file.evidenceCount} {file.evidenceCount === 1 ? 'file' : 'files'}
                </span>
              )}
              <span className="flex-1" />
              <span className="text-muted-foreground">
                <span className="font-mono">{file.bannedAt ? formatDay(file.bannedAt) : formatDay(file.createdAt)}</span> ·{' '}
                {file.authorUsername}
              </span>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  )
}
