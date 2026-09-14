import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
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

  if (error) return null
  if (!list) return null

  return (
    <div
      className="rounded-md border px-3 py-2"
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      <div className="font-medium">Case files</div>

      {list.cases.length === 0 ? (
        <p className="mt-1 text-muted-foreground">No case files.</p>
      ) : (
        <ul className="mt-1 flex flex-col gap-1">
          {list.cases.map((file) => (
            <li key={file.id} className="flex flex-wrap items-center gap-x-2 gap-y-1">
              <button
                type="button"
                onClick={() => go(`/cases/${file.id}`)}
                className="rounded text-left font-medium hover:underline focus-visible:outline-2 focus-visible:outline-ring"
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
                {file.bannedAt ? formatDay(file.bannedAt) : formatDay(file.createdAt)} · {file.authorUsername}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
