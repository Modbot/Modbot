import { useState } from 'react'
import { openReference, uniqueSources } from '@/components/chat/sourceLinks'
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
        <button
          type="button"
          onClick={() => setAll(true)}
          className="rounded-full border px-2.5 py-0.5 text-muted-foreground hover:bg-accent hover:text-accent-foreground"
          style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
        >
          +{sources.length - shown.length}
        </button>
      )}
    </div>
  )
}

export function SourceChip({ reference }: { reference: ChatReference }) {
  return (
    <button
      type="button"
      title={reference.id}
      onClick={() => openReference(reference)}
      className="max-w-[16rem] truncate rounded-full border bg-card px-2.5 py-0.5 font-medium hover:bg-accent hover:text-accent-foreground focus-visible:outline-2 focus-visible:outline-ring"
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      {reference.label ?? reference.id}
    </button>
  )
}
