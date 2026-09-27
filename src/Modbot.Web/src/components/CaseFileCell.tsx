import { useState } from 'react'
import { Pencil } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { WriteCaseFile } from '@/components/CaseFileForm'
import type { CaseFileLookup } from '@/lib/api'
import { cn } from '@/lib/utils'

/**
 * The case file column on a ban list: a badge and the one button that matters.
 *
 * A ban with a case file opens it; a ban without one shows a dash, and a pencil to write it for
 * whoever may ban. The two are deliberately the same column — the question a moderator has looking
 * at a ban list is "is there an account of this anywhere", and the dash answers it as plainly as
 * the button does. The ban list's Case file filter narrows to either.
 */
export function CaseFileCell({
  userId,
  displayName,
  bannedAt,
  auditEntryId,
  lookup,
  canWrite,
  onOpenCase,
}: {
  userId: string
  displayName: string | null
  bannedAt: string | null
  auditEntryId?: string | null
  /** What the lookup said about this person, or undefined while it is still in flight. */
  lookup: CaseFileLookup | undefined
  canWrite: boolean
  onOpenCase: (caseId: string) => void
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
        {lookup ? (lookup.count > 0 ? 'withdrawn' : '—') : ''}
      </span>
    )
  }

  // The same words on every row of a list where most bans have none was a column of noise
  // (site review 2026-09-27 §11). A dash says "none"; the pencil shows on the row a pointer rests
  // on, and always where there is no hover (a phone, a headset) or when the keyboard reaches it.
  return (
    <>
      <span className="inline-flex items-center gap-1.5">
        <span className="text-muted-foreground" aria-hidden>
          —
        </span>
        <Button
          variant="ghost"
          size="xs"
          aria-label="Write the case file"
          title="Write the case file"
          className={cn(
            'text-muted-foreground opacity-0 transition hover:text-foreground',
            'focus-visible:opacity-100 group-hover/row:opacity-100 [@media(hover:none)]:opacity-100 headset:opacity-100',
            writing && 'opacity-100',
          )}
          onClick={() => setWriting(true)}
        >
          <Pencil className="size-3.5" />
        </Button>
      </span>

      <Dialog open={writing} onOpenChange={setWriting}>
        <DialogContent title="Write the case file" className="max-w-[640px]">
          <WriteCaseFile
            ban={{ userId, displayName, bannedAt, auditEntryId: auditEntryId ?? null }}
            onCancel={() => setWriting(false)}
            onWritten={(caseId) => {
              setWriting(false)
              onOpenCase(caseId)
            }}
          />
        </DialogContent>
      </Dialog>
    </>
  )
}
