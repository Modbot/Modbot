import { DialogContent } from '@/components/ui/dialog'
import { FactSentence } from '@/components/factSentence'
import { FactTime, SourceBadge } from '@/components/facts'
import type { AuditEntry } from '@/lib/api'
import { cn } from '@/lib/utils'

/**
 * The pieces the three popups share: one loader, one way of stating a figure, one fact list.
 *
 * Kept as small parts rather than a popup template, because a person, a world and an instance are
 * genuinely different and a template would pull them towards being the same screen.
 */

/** One labelled fact about the thing on the left of a popup. */
export function Field({
  label,
  children,
  title,
}: {
  label: string
  children: React.ReactNode
  title?: string
}) {
  return (
    <div className="flex flex-col gap-0.5" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <span className="break-words" title={title}>
        {children}
      </span>
    </div>
  )
}

/** A number worth reading at a glance, with the caveat that belongs to it underneath. */
export function Figure({ label, value, note }: { label: string; value: string; note?: string }) {
  return (
    <div
      className="rounded-md border px-3 py-2"
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      <div className="text-muted-foreground">{label}</div>
      <div className="mt-0.5 font-mono text-lg font-medium tracking-tight tabular-nums">{value}</div>
      {note && <div className="text-muted-foreground">{note}</div>}
    </div>
  )
}

export function Note({ children, className }: { children: React.ReactNode; className?: string }) {
  return (
    <p className={cn('text-muted-foreground', className)} style={{ fontSize: 'var(--text-small)' }}>
      {children}
    </p>
  )
}

export function Panel({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="flex flex-col gap-2 p-4">
      <div className="font-medium">{title}</div>
      {children}
    </section>
  )
}

/**
 * A list of facts, each as a sentence with every name in it clickable.
 *
 * The same list on a person's popup and on an instance's, because they are the same thing seen through
 * two different filters, and a reader should not have to learn two layouts for it.
 *
 * `from` names the account a row was found under, for the one list that merges several — a
 * person's VRChat account, their Discord account and their Modbot account are three histories in
 * one timeline, and a row that does not say which one it came from would be claiming the merge
 * proves more than it does (one view per person design §4).
 */
export function FactList({
  entries,
  empty,
  from,
}: {
  entries: AuditEntry[]
  empty: string
  from?: (entry: AuditEntry) => string | undefined
}) {
  if (entries.length === 0) return <Note>{empty}</Note>

  return (
    <ol className="flex flex-col gap-2">
      {entries.map((entry) => (
        <li
          key={entry.id}
          className="rounded-md border px-3 py-2"
          style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
        >
          <div className="flex items-center gap-2">
            <SourceBadge source={entry.source} />
            {from?.(entry) && <span className="text-muted-foreground">{from(entry)}</span>}
            <span className="flex-1" />
            <FactTime entry={entry} />
          </div>
          <div className="mt-1">
            <FactSentence entry={entry} />
          </div>
        </li>
      ))}
    </ol>
  )
}

/**
 * The frame every popup shares: identity on the left, tabs on the right.
 *
 * Nearly the whole window, because every kind now carries an Overview, a History and a JSON tab
 * beside what it had, and a raw record or a table of versions wants room. Stacks to one column
 * on a narrow screen, where the whole popup scrolls rather than each column.
 */
export function PopupFrame({
  title,
  subtitle,
  lead,
  actions,
  left,
  children,
}: {
  title: string
  subtitle?: React.ReactNode
  lead?: React.ReactNode
  actions?: React.ReactNode
  left: React.ReactNode
  children: React.ReactNode
}) {
  return (
    <DialogContent
      title={title}
      subtitle={subtitle}
      lead={lead}
      actions={actions}
      aria-describedby={undefined}
      className="h-[calc(100dvh-2rem)] w-[calc(100vw-2rem)] max-w-[100rem]"
      bodyClassName="grid overflow-auto p-0 md:grid-cols-[22rem_minmax(0,1fr)] md:overflow-hidden"
    >
      <aside
        className="flex flex-col gap-3 border-b p-4 md:overflow-auto md:border-r md:border-b-0"
        style={{ borderWidth: 0, borderRightWidth: 'var(--hairline)', borderBottomWidth: 'var(--hairline)' }}
      >
        {left}
      </aside>
      <div className="flex min-h-0 flex-col">{children}</div>
    </DialogContent>
  )
}

