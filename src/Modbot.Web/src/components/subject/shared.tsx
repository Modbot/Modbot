import { CardAction, CardContent, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { DialogContent } from '@/components/ui/dialog'
import { EmptyRow } from '@/components/PanelGrid'
import { FactSentence } from '@/components/factSentence'
import { FactTime, ReportedBy, SourceBadge } from '@/components/facts'
import type { AuditEntry } from '@/lib/api'
import { cn } from '@/lib/utils'

/**
 * The pieces the three popups share: one loader, one section, one fact list.
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

export function Note({ children, className }: { children: React.ReactNode; className?: string }) {
  return (
    <p className={cn('text-muted-foreground', className)} style={{ fontSize: 'var(--text-small)' }}>
      {children}
    </p>
  )
}

/** One section of a popup: its name on a strip, what it holds below, one hairline under it. */
export function Panel({
  title,
  right,
  flush = false,
  warn = false,
  className,
  children,
}: {
  title: string
  right?: React.ReactNode
  /** Runs the content to the section's edges, for a list or a table. */
  flush?: boolean
  /** Tints the strip, when what it says on the right is that the section cannot be trusted yet. */
  warn?: boolean
  className?: string
  children: React.ReactNode
}) {
  return (
    <section className={cn('shrink-0 border-b border-b-(length:--hairline)', className)}>
      <CardHeader className={cn(warn && 'bg-warn/10')}>
        <CardTitle>{title}</CardTitle>
        {right && <CardAction>{right}</CardAction>}
      </CardHeader>
      {flush ? <div>{children}</div> : <CardContent className="flex flex-col gap-2">{children}</CardContent>}
    </section>
  )
}

/** A block of a popup with no name of its own, the identity at the top of the left column. */
export function Block({ children, className }: { children: React.ReactNode; className?: string }) {
  return (
    <div className={cn('flex shrink-0 flex-col gap-3 border-b border-b-(length:--hairline) p-(--panel-pad)', className)}>
      {children}
    </div>
  )
}

/** A one-line state of a popup section (loading, empty, failed), ruled off like a section. */
export function Empty({ children, className }: { children: React.ReactNode; className?: string }) {
  return <EmptyRow className={cn('shrink-0 border-b border-b-(length:--hairline)', className)}>{children}</EmptyRow>
}

/** The strip along the foot of a section's list: what the list shows, and how fresh it is. */
export function Footer({ children }: { children: React.ReactNode }) {
  return (
    <CardFooter className="flex-wrap gap-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
      {children}
    </CardFooter>
  )
}

/** The control on the right of a section's strip that opens the tab holding the rest. */
export function More({ onClick, children }: { onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="rounded-sm text-muted-foreground hover:text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring"
      style={{ fontSize: 'var(--text-small)' }}
    >
      {children}
    </button>
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
  if (entries.length === 0) return <EmptyRow>{empty}</EmptyRow>

  return (
    <ol className="flex flex-col">
      {entries.map((entry) => (
        <li
          key={entry.id}
          className="border-t border-t-(length:--hairline) px-(--panel-pad) py-2 first:border-t-0"
          style={{ fontSize: 'var(--text-small)' }}
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
          <ReportedBy entry={entry} />
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
 *
 * On a phone it is the screen, edge to edge, with no gutter and no corners. This is the screen a
 * moderator spends the most time on, and a popup floating inside a 16px margin spends 32px of a
 * 390px screen on the page behind it, which they are not reading.
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
      className={cn(
        'top-0 left-0 h-[100dvh] max-h-none w-screen max-w-none translate-x-0 translate-y-0 rounded-none border-0',
        'md:top-1/2 md:left-1/2 md:h-[calc(100dvh-2rem)] md:w-[calc(100vw-2rem)] md:max-w-[100rem]',
        'md:-translate-x-1/2 md:-translate-y-1/2 md:rounded-sm md:border',
      )}
      // `minmax(0,1fr)` on the one-column case as well: a bare `grid` sizes its column to the
      // widest thing in it, so the stacked popup was as wide as its widest table and scrolled
      // sideways as a whole rather than letting the table scroll inside itself.
      bodyClassName="grid grid-cols-[minmax(0,1fr)] overflow-auto p-0 md:grid-cols-[22rem_minmax(0,1fr)] md:overflow-hidden"
    >
      {/* Every block in the column draws the hairline under itself, so the column draws only
          the one beside it. */}
      <aside className="flex flex-col md:overflow-auto md:border-r md:border-r-(length:--hairline)">
        {left}
      </aside>
      <div className="flex min-h-0 flex-col">{children}</div>
    </DialogContent>
  )
}

