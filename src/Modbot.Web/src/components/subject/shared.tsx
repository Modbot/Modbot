import { useEffect, useState } from 'react'
import { Braces, Check, Copy, MoreHorizontal } from 'lucide-react'
import { Popover } from 'radix-ui'
import { CardAction, CardContent, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { DialogContent } from '@/components/ui/dialog'
import { Tabs } from '@/components/ui/tabs'
import { EmptyRow } from '@/components/PanelGrid'
import { FactSentence } from '@/components/factSentence'
import { FactTime, ReportedBy, SourceBadge } from '@/components/facts'
import type { AuditEntry } from '@/lib/api'
import { factDays } from '@/lib/factRows'
import { usePhoneLayout } from '@/lib/phoneLayout'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'

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

/**
 * A world's picture beside the title, for the World and Instance popups on a phone, where the
 * desk's picture across the left column was most of the first screen. 4:3, as VRChat draws it.
 * Left out of a popup opened from another one: beside the way back it left the title about 28px
 * on a 390px phone.
 *
 * `undefined` while the page is still being read, which holds the picture's place with an empty
 * frame so the title does not jump; `null` for a world with no picture, which draws nothing.
 */
export function HeaderPicture({ url }: { url: string | null | undefined }) {
  if (url === undefined) return <span className="aspect-[4/3] h-10 shrink-0 rounded-sm bg-muted" />
  const src = vrchatMedia(url)
  return src ? <img src={src} alt="" className="aspect-[4/3] h-10 shrink-0 rounded-sm bg-muted object-cover" /> : null
}

/**
 * A one-line state of a popup section (loading, empty, failed), ruled off like a section.
 * `danger` for a read that failed, so it does not look like an empty section.
 */
export function Empty({
  children,
  className,
  tone,
}: {
  children: React.ReactNode
  className?: string
  tone?: 'neutral' | 'danger'
}) {
  return (
    <EmptyRow tone={tone} className={cn('shrink-0 border-b border-b-(length:--hairline)', className)}>
      {children}
    </EmptyRow>
  )
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
 *
 * Rows sit under a heading per day, and one thing two sources both recorded is one row with both
 * badges (`lib/factRows`). `now` is the server's clock, for "Today" and "Yesterday".
 */
export function FactList({
  entries,
  empty,
  now,
  from,
}: {
  entries: AuditEntry[]
  empty: string
  now: string
  from?: (entry: AuditEntry) => string | undefined
}) {
  if (entries.length === 0) return <EmptyRow>{empty}</EmptyRow>

  return (
    <ol className="flex flex-col">
      {factDays(entries, now).map((day, i) => (
        <li key={`${day.key}:${i}`} className="border-t border-t-(length:--hairline) first:border-t-0">
          <h3
            className="bg-muted/40 px-(--panel-pad) py-1 font-medium text-muted-foreground"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {day.heading}
          </h3>
          <ol className="flex flex-col">
            {day.rows.map(({ entry, also }) => (
              <li
                key={entry.id}
                className="border-t border-t-(length:--hairline) px-(--panel-pad) py-2"
                style={{ fontSize: 'var(--text-small)' }}
              >
                <div className="flex items-center gap-2">
                  <span className="flex flex-wrap items-center gap-1">
                    {[entry, ...also].map((seen) => (
                      <SourceBadge key={seen.id} source={seen.source} />
                    ))}
                  </span>
                  {from?.(entry) && <span className="text-muted-foreground">{from(entry)}</span>}
                  <span className="flex-1" />
                  <FactTime entry={entry} />
                </div>
                <div className="mt-1">
                  <FactSentence entry={entry} />
                </div>
                {[entry, ...also].map((seen) => (
                  <ReportedBy key={seen.id} entry={seen} />
                ))}
              </li>
            ))}
          </ol>
        </li>
      ))}
    </ol>
  )
}

/**
 * An id under a popup's title, cut short to fit, as one button that copies the whole of it.
 *
 * The title is the name, which is what a moderator reads; the id is what they paste into a report
 * or a search, so it is there to be copied rather than read. Shown verbatim and never parsed: a
 * legacy VRChat id looks nothing like a modern one (spec 3.1.1). A browser that refuses the
 * clipboard says so.
 */
export function CopyId({ id }: { id: string }) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle')

  useEffect(() => {
    if (state === 'idle') return
    const timer = window.setTimeout(() => setState('idle'), 2000)
    return () => window.clearTimeout(timer)
  }, [state])

  const copy = () => {
    const write = navigator.clipboard?.writeText(id)
    if (!write) {
      setState('failed')
      return
    }
    write.then(
      () => setState('copied'),
      () => setState('failed'),
    )
  }

  return (
    <span className="inline-flex min-w-0 items-center gap-1.5">
      <button
        type="button"
        onClick={copy}
        title={id}
        aria-label="Copy id"
        // The line of text is 20px tall, too small for a finger, and a taller button would push the
        // header down. So the part that takes a tap reaches past it, to a control's height.
        className="relative inline-flex min-w-0 items-center gap-1 rounded-sm px-1 -mx-1 text-muted-foreground outline-none after:absolute after:inset-x-0 after:inset-y-[calc((100%-var(--control-h))/2)] hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring"
      >
        <span className="max-w-[14rem] truncate font-mono">{id}</span>
        {state === 'copied' ? <Check className="size-3 shrink-0 text-ok" /> : <Copy className="size-3 shrink-0" />}
      </button>
      {state === 'failed' && <span className="text-destructive">Could not copy</span>}
    </span>
  )
}

/**
 * The frame every popup shares: identity on the left, tabs on the right.
 *
 * Nearly the whole window, because every kind now carries an Overview, a History and its raw data
 * beside what it had, and a raw record or a table of versions wants room. Stacks to one column
 * on a narrow screen, where the whole popup scrolls rather than each column.
 *
 * On a phone it is the screen, edge to edge, with no gutter and no corners. This is the screen a
 * moderator spends the most time on, and a popup floating inside a 16px margin spends 32px of a
 * 390px screen on the page behind it, which they are not reading.
 *
 * Which of the two it is comes from the `big` variant rather than from `md`, so a phone on its
 * side keeps the one column and the pinned foot (`lib/phoneLayout.ts`).
 */
export function PopupFrame({
  title,
  subtitle,
  lead,
  actions,
  standing,
  foot,
  left,
  children,
}: {
  title: string
  subtitle?: React.ReactNode
  lead?: React.ReactNode
  actions?: React.ReactNode
  /** One row across both columns, straight under the title: what the reader must see first. */
  standing?: React.ReactNode
  /** A row pinned to the foot of the screen on a phone, for the controls a thumb needs. */
  foot?: React.ReactNode
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
      place="full"
      foot={foot ?<div className="shrink-0 big:hidden">{foot}</div> : undefined}
      className={cn(
        'top-0 left-0 h-[100dvh] max-h-none w-screen max-w-none translate-x-0 translate-y-0 rounded-none border-0',
        'big:top-1/2 big:left-1/2 big:h-[calc(100dvh-2rem)] big:w-[calc(100vw-2rem)] big:max-w-[100rem]',
        'big:-translate-x-1/2 big:-translate-y-1/2 big:rounded-sm big:border',
      )}
      // `minmax(0,1fr)` on the one-column case as well: a bare `grid` sizes its column to the
      // widest thing in it, so the stacked popup was as wide as its widest table and scrolled
      // sideways as a whole rather than letting the table scroll inside itself.
      // `auto-rows-max` because the body is a fixed height: its rows were sized to the tabs'
      // minimum height rather than to what they hold, and the rest of a long tab spilled out of a
      // row that ended a screen down, taking the pinned tab row's hold with it.
      bodyClassName={cn(
        'grid auto-rows-max grid-cols-[minmax(0,1fr)] content-start overflow-auto p-0 big:auto-rows-auto big:grid-cols-[22rem_minmax(0,1fr)] big:overflow-hidden',
        standing ? 'big:grid-rows-[auto_minmax(0,1fr)]' : 'big:grid-rows-[minmax(0,1fr)]',
      )}
    >
      {standing && <div className="big:col-span-2">{standing}</div>}
      {/* Every block in the column draws the hairline under itself, so the column draws only
          the one beside it. */}
      <aside className="flex flex-col big:overflow-auto big:border-r big:border-r-(length:--hairline)">
        {left}
      </aside>
      {/* On a phone the tabs are at least a screen tall. Left to size themselves, they were
          squeezed into whatever the left column left over -- a few rows under a long profile --
          and the popup never scrolled, so the tab a reader had just opened stayed out of sight. */}
      <div className="flex min-h-[calc(100dvh-6rem)] flex-col big:min-h-0">{children}</div>
    </DialogContent>
  )
}

/**
 * A popup's tabs. On a desk the tab scrolls on its own beside the left column. On a phone the
 * popup is the one scroll: the row stays pinned under the header, the tab grows with the popup,
 * and every tab stays in sight on two lines rather than one running off the edge. A tab that
 * scrolled on its own inside a popup that also scrolled showed about 50px of itself (mobile review
 * 2026-09-28, #4). `flex-auto` there rather than `flex-1`, so each part is as tall as what it
 * holds and the pinned row is held for the whole length of the tab.
 *
 * `at` is the ref from `useOpenFromAbove`, and `onChange` its `pick`.
 */
export function PopupTabs<T extends string>({
  at,
  value,
  onChange,
  tabs,
  children,
}: {
  at: React.Ref<HTMLDivElement>
  value: T
  onChange: (next: T) => void
  tabs: { value: T; label: string; badge?: number | null }[]
  children: React.ReactNode
}) {
  const phone = usePhoneLayout()

  return (
    <div ref={at} className="flex min-h-0 flex-auto flex-col big:flex-1">
      <Tabs
        value={value}
        onChange={onChange}
        tabs={tabs}
        wrap={phone}
        className="flex-auto big:flex-1"
        rowClassName="sticky top-0 z-10 bg-card big:static"
        panelClassName="flex-auto overflow-visible big:flex-1 big:overflow-auto"
      >
        {children}
      </Tabs>
    </div>
  )
}

/**
 * The ⋯ in the header, for what a moderator seldom needs and the tab row has no room for.
 *
 * Raw data was a tab of its own on every popup, and on a phone it pushed the tabs people use off the
 * screen. While it is open it has a tab like the rest, so the row still says where the reader is.
 */
export function PopupMenu({ onRawData }: { onRawData: () => void }) {
  const [open, setOpen] = useState(false)

  return (
    <Popover.Root open={open} onOpenChange={setOpen}>
      <Popover.Trigger asChild>
        {/* Sized like the close button beside it. */}
        <button
          type="button"
          aria-label="More"
          className="grid shrink-0 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring"
          style={{ height: 'var(--control-h)', width: 'var(--control-h)' }}
        >
          <MoreHorizontal className="size-4" />
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content
          align="end"
          sideOffset={4}
          className="z-50 flex flex-col rounded-sm border border-(length:--hairline) bg-popover p-1 text-popover-foreground shadow-sm"
          style={{ fontSize: 'var(--text-small)' }}
        >
          <button
            type="button"
            onClick={() => {
              setOpen(false)
              onRawData()
            }}
            className="flex items-center gap-2 rounded-sm px-2 py-1.5 text-left transition-colors hover:bg-muted"
          >
            <Braces className="size-3.5" />
            Raw data
          </button>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  )
}

