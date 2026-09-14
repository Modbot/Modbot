import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { WriteCaseFile } from '@/components/CaseFileForm'
import type { CaseFileLookup } from '@/lib/api'

/**
 * The case file column on a ban list: a badge and the one button that matters.
 *
 * A ban with a case file opens it; a ban without one offers to write it, to whoever may ban. The
 * two are deliberately the same column — the question a moderator has looking at a ban list is
 * "is there an account of this anywhere", and an absence has to be as visible as a presence.
 */
export function CaseFileCell({
  userId,
  displayName,
  bannedAt,
  auditEntryId,
  lookup,
  canWrite,
  onOpenCase,
  onWritten,
}: {
  userId: string
  displayName: string | null
  bannedAt: string | null
  auditEntryId?: string | null
  /** What the lookup said about this person, or undefined while it is still in flight. */
  lookup: CaseFileLookup | undefined
  canWrite: boolean
  onOpenCase: (caseId: string) => void
  onWritten: () => void
}) {
  const [writing, setWriting] = useState(false)

  if (lookup?.caseId) {
    return (
      <Button variant="outline" size="xs" onClick={() => onOpenCase(lookup.caseId!)}>
        Open the case file
        {lookup.count > 1 ? ` (${lookup.count})` : ''}
      </Button>
    )
  }

  if (!canWrite) {
    return (
      <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {lookup ? (lookup.count > 0 ? 'withdrawn' : 'none') : ''}
      </span>
    )
  }

  return (
    <>
      <Button variant="ghost" size="xs" className="text-muted-foreground" onClick={() => setWriting(true)}>
        Write the case file
      </Button>

      <Dialog open={writing} onOpenChange={setWriting}>
        <DialogContent title="Write the case file" className="max-w-[640px]">
          <WriteCaseFile
            ban={{ userId, displayName, bannedAt, auditEntryId: auditEntryId ?? null }}
            onCancel={() => setWriting(false)}
            onWritten={(caseId) => {
              setWriting(false)
              onWritten()
              onOpenCase(caseId)
            }}
          />
        </DialogContent>
      </Dialog>
    </>
  )
}
