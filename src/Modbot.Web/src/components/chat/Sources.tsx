import { useState } from 'react'
import { openReference, uniqueSources } from '@/components/chat/sourceLinks'
import { Badge } from '@/components/ui/badge'
import type { ChatReference } from '@/lib/api'

/** How many sources are shown before the rest fold behind a count. */
const SHOWN = 10

/**
 * What an answer was built from, under it.
 *
 * A moderator acting on an answer has to be able to check it, and the steps above only say which
 * lookups ran. These are the rows themselves -- the ban, the case file, the message -- each opening
 * where it lives.
 */
export function Sources({ references }: { references: readonly ChatReference[] }) {
  const [all, setAll] = useState(false)

  const sources = uniqueSources(references)
  if (sources.length === 0) return null

  const shown = all ? sources : sources.slice(0, SHOWN)

  return (
    <div className="mt-1 flex flex-wrap items-center gap-1.5">
      <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        Sources
      </span>
      {shown.map((reference) => (
        <SourceChip key={`${reference.kind}:${reference.id}`} reference={reference} />
      ))}
      {!all && sources.length > shown.length && (
        <Badge variant="outline" asChild className="font-mono hover:bg-muted hover:text-foreground">
          <button type="button" onClick={() => setAll(true)}>
            +{sources.length - shown.length}
          </button>
        </Badge>
      )}
    </div>
  )
}

export function SourceChip({ reference }: { reference: ChatReference }) {
  return (
    <Badge variant="outline" asChild className="block max-w-[16rem] truncate bg-card text-foreground hover:bg-muted">
      <button type="button" title={reference.id} onClick={() => openReference(reference)}>
        {reference.label ?? reference.id}
      </button>
    </Badge>
  )
}
