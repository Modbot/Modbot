import type { SweepCoverage } from '@/lib/api'
import { ago } from '@/lib/format'

/**
 * How old a swept list is, stated on the strip at the top of it: the member list, the ban list.
 * Before the first full sweep the list is partial, so this says so after a warning square and the
 * caller turns the strip the warning colour, because a short list shown as the whole group is the
 * mistake these pages most need to not make.
 */
export function Freshness({
  coverage,
  count,
  list,
  noun,
  demo,
}: {
  coverage: SweepCoverage
  /** How many the last full sweep found. */
  count: number
  /** What the list is called in a sentence: "member list". */
  list: string
  /** One of what it holds: "member", counted with an s. */
  noun: string
  demo: boolean
}) {
  const counted = (
    <>
      <span className="font-mono">{count.toLocaleString()}</span> {count === 1 ? noun : `${noun}s`}
    </>
  )

  // A demo's list was filled in rather than read, so there is no sync time to state and nothing
  // is waiting to be read.
  if (demo) {
    return (
      <div className="flex flex-wrap items-baseline gap-x-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        <span>Demo data.</span>
        <span>{counted}.</span>
      </div>
    )
  }

  if (!coverage.firstSweepComplete) {
    return (
      <Unread>
        {coverage.sweepInProgress ? `Reading the ${list} for the first time.` : `The ${list} has not been read yet.`}
      </Unread>
    )
  }

  return (
    <div className="flex flex-wrap items-baseline gap-x-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
      <span>
        Last synced <Ago iso={coverage.lastSyncedAt} now={coverage.now} />
        {coverage.sweepInProgress ? '. A new sweep is running now' : ''}.
      </span>
      <span>{counted} at the last full sweep.</span>
    </div>
  )
}

/**
 * How long ago something happened, said inside a sentence: "Last synced 9h ago". The time is a
 * reading, so it is set in mono like every other timestamp; with no time the sentence says "never",
 * which is a word and stays in the sentence's own face.
 */
export function Ago({ iso, now }: { iso: string | null; now: string }) {
  const text = ago(iso, now)
  return iso ? <span className="font-mono">{text}</span> : text
}

/** A list that cannot be trusted yet, said on its strip after a warning square. */
export function Unread({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex items-center gap-2 font-medium">
      <span aria-hidden className="size-2 shrink-0 bg-warn" />
      {children}
    </div>
  )
}
