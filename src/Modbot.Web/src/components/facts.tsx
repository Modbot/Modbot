import { cn } from '@/lib/utils'
import { sourceLabel } from '@/lib/format'
import type { AuditEntry } from '@/lib/api'

/**
 * The pieces every screen that shows a fact reuses.
 *
 * One place, because the two things most easily got wrong — how a source is attributed, and how an
 * imprecise time is displayed — have to be got right identically on the audit log, the ban list
 * and the subject pane.
 */

/** Sources get distinct, stable colours so the merged timeline is readable without reading. */
const SOURCE_SERIES: Record<string, number> = {
  AuditLog: 1,
  SyncDiff: 3,
  Client: 4,
  Discord: 5,
  Manual: 2,
  Modbot: 2,
}

/** Which system said so. The colour is a second channel; the label carries the identity. */
export function SourceBadge({ source, className }: { source: string; className?: string }) {
  const series = SOURCE_SERIES[source] ?? 1

  return (
    <span
      className={cn('inline-flex shrink-0 items-center gap-1.5 rounded-full border px-2 py-0.5', className)}
      style={{ fontSize: 'var(--text-small)', borderWidth: 'var(--hairline)' }}
    >
      <span className="size-1.5 shrink-0 rounded-full" style={{ background: `var(--series-${series})` }} />
      {sourceLabel(source)}
    </span>
  )
}

const time = (iso: string) =>
  new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })

const dateTime = (iso: string) => new Date(iso).toLocaleString()

/**
 * When a fact happened — as an instant when that is known, and as a range when it is not.
 *
 * Spec 5.3: a sync diff knows only that something happened between two polls. Collapsing that to
 * the lower bound invents precision Modbot does not have, and the invention is invisible — the
 * timestamp looks exactly like one VRChat stated. So a windowed fact is rendered as a window, with
 * the tilde carrying the claim even when the row is scanned rather than read.
 */
export function FactTime({ entry }: { entry: Pick<AuditEntry, 'occurredAt' | 'occurredBefore'> }) {
  if (!entry.occurredBefore) {
    return (
      <span className="tabular-nums text-muted-foreground" title={dateTime(entry.occurredAt)}>
        {time(entry.occurredAt)}
      </span>
    )
  }

  return (
    <span
      className="tabular-nums text-muted-foreground"
      title={`Sometime between ${dateTime(entry.occurredAt)} and ${dateTime(entry.occurredBefore)} — Modbot inferred this from a change between two syncs and cannot know when inside that window it happened.`}
    >
      ~{time(entry.occurredAt)}–{time(entry.occurredBefore)}
    </span>
  )
}

/**
 * A person, as a launcher for their pane.
 *
 * Spec 10.2: every list that renders a person is a launcher, so there is one component, one fetch
 * shape, and one place to add anything new about a person.
 */
export function SubjectLink({
  id,
  name,
  onOpen,
  className,
}: {
  id: string
  name?: string | null
  onOpen: (id: string) => void
  className?: string
}) {
  return (
    <button
      type="button"
      onClick={() => onOpen(id)}
      title={id}
      className={cn(
        'max-w-[18rem] truncate rounded text-left hover:underline focus-visible:outline-2 focus-visible:outline-ring',
        name ? 'font-medium' : 'font-mono',
        className,
      )}
    >
      {name ?? id}
    </button>
  )
}
