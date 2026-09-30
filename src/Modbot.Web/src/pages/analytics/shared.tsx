import { useState } from 'react'
import type { LucideIcon } from 'lucide-react'
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { dayRange, longDay } from '@/components/charts'
import { Ago } from '@/components/Freshness'
import { Row } from '@/components/ui/fact-row'
import type { AnalyticsCoverage } from '@/lib/api'
import { lengthOfTime, needsYear } from '@/lib/format'
import type { PageId } from '@/lib/nav'
import { followLink } from '@/lib/router'
import { SwitchBank } from '@/components/ui/switch-bank'
import { cn } from '@/lib/utils'
import { sectionId } from './sectionId'
import { RANGES, type Range } from './useAnalytics'

/**
 * What the Stats page's parts and the platform pages share: the date-range control, the panel and
 * stat frames, the loading and error states, and the line that says where the numbers came from.
 *
 * Kept as plain building blocks rather than a page template, because the parts are meant to be
 * different from each other -- one question each (spec 10.1) -- and a template would pull them
 * back towards the single dashboard they replaced.
 */

export function RangePicker({
  range,
  onChange,
  from,
  to,
}: {
  range: Range
  onChange: (r: Range) => void
  from?: string
  to?: string
}) {
  return (
    <div className="flex flex-wrap items-center gap-2">
      <SwitchBank
        label="Range"
        value={String(range)}
        onChange={(v) => onChange(RANGES.find((r) => String(r.range) === v)!.range)}
        options={RANGES.map((r) => ({ value: String(r.range), label: r.label }))}
      />
      <span className="flex-1" />
      {from && to && (
        <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {dayRange(from, to)}
        </span>
      )}
    </div>
  )
}

/**
 * One reading: its name, then the number large under it, then any note small under that. The
 * number comes straight after the name, so the numbers of a row line up whether or not they carry
 * a note, and the eye lands on the number before the date beside it -- which, set on one line with
 * the number, it used to read first. On its own it draws its own edge; inside a StatStrip it
 * shares its edges with the others.
 *
 * The sizes and the padding are custom properties with the desktop values as fallbacks, so the
 * phone rule in index.css can make a strip of them compact without a second layout.
 *
 * `noteMono` for a note that is a machine value (a day, a time, a share), as on `Row`; a note
 * that is a sentence stays in the body face, with any time in it set in mono by the caller.
 */
export function Stat({
  label,
  value,
  note,
  noteMono = false,
}: {
  label: string
  value: React.ReactNode
  note?: React.ReactNode
  noteMono?: boolean
}) {
  return (
    <div
      data-slot="stat"
      className="flex min-w-0 flex-col border border-(length:--hairline) bg-card px-(--panel-pad) pt-2 pb-2.5"
      style={{ minHeight: 'var(--stat-min-h, calc(var(--row-h) * 2))' }}
    >
      <div className="break-words text-muted-foreground" style={{ fontSize: 'var(--stat-label, var(--text-small))' }}>
        {label}
      </div>
      <div
        className="break-words font-mono leading-none font-medium tracking-tight"
        style={{ fontSize: 'var(--stat-value, calc(var(--text-base) * 1.75))', paddingTop: 'var(--stat-gap, 0.625rem)' }}
      >
        {value}
      </div>
      {note && (
        <div
          className={cn('min-w-0 break-words text-muted-foreground', noteMono && 'font-mono')}
          style={{ fontSize: 'var(--stat-note, var(--text-small))', paddingTop: 'var(--stat-note-gap, 0.375rem)' }}
        >
          {note}
        </div>
      )}
    </div>
  )
}

/**
 * A row of Stats sharing one hairline grid. Two across on a narrow screen and the caller's count
 * when wider; on a phone, three compact ones across (index.css), so a strip of six is two short
 * rows and the first chart is on the first screen.
 *
 * `phonePairs` for a strip of two, four or five, which three across leaves with a lone tile or an
 * empty slot: on a phone it goes two across, and an odd first tile takes the whole row. Two across
 * also leaves a date and time room for one line.
 */
export function StatStrip({
  className,
  phonePairs = false,
  children,
}: {
  className?: string
  phonePairs?: boolean
  children: React.ReactNode
}) {
  return (
    <PanelGrid className={cn('grid-cols-2 xl:grid-cols-4', className)} phonePairs={phonePairs}>
      {children}
    </PanelGrid>
  )
}

/**
 * A named part of a page: a small heading with a rule running out from it, as the Live page heads
 * each platform's half, and the panels under it. With `href`, the heading is a link to where the
 * part is shown in full, as each platform page's This week opens Stats.
 */
export function Section({ title, href, children }: { title: string; href?: string; children: React.ReactNode }) {
  const id = sectionId(title)

  return (
    <section aria-labelledby={id} className="flex flex-col gap-2">
      {/* The margin keeps a heading jumped to clear of the sticky top bar. */}
      <h2 id={id} className="flex scroll-mt-20 items-center gap-2 font-label text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {href ? (
          <a
            href={href}
            onClick={followLink(href)}
            className="rounded-sm underline-offset-2 hover:text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-ring"
          >
            {title}
          </a>
        ) : (
          title
        )}
        <span aria-hidden className="h-(--hairline) flex-1 bg-border" />
      </h2>
      {children}
    </section>
  )
}

/**
 * A row of links along the foot of a page's header, set like tabs: each leads to the Modbot page
 * that shows that part, and the page itself is marked. The caller leaves out what the person may
 * not open. Scrolls sideways on a narrow screen rather than wrapping, like the Tabs component,
 * unless `wrap` asks for every tab to stay in sight on more than one line.
 */
export function HeaderTabs({
  label,
  tabs,
  active,
  pathOf,
  wrap = false,
}: {
  label: string
  tabs: readonly { id: PageId; label: string; icon?: LucideIcon }[]
  active: PageId
  pathOf: (id: PageId) => string
  wrap?: boolean
}) {
  return (
    <nav aria-label={label} className="border-t border-t-(length:--hairline)">
      <div className={cn('flex items-stretch px-1', wrap ? 'flex-wrap' : 'overflow-x-auto [scrollbar-width:thin]')}>
        {tabs.map((tab) => {
          const current = tab.id === active
          const href = pathOf(tab.id)

          return (
            <a
              key={tab.id}
              href={href}
              onClick={followLink(href)}
              aria-current={current ? 'page' : undefined}
              className={cn(
                'relative flex shrink-0 items-center gap-1.5 px-3 font-medium whitespace-nowrap transition-colors focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring',
                current ? 'text-foreground' : 'text-muted-foreground hover:text-foreground',
              )}
              style={{ fontSize: 'var(--text-base)', minHeight: 'var(--control-h)' }}
            >
              {tab.icon && <tab.icon aria-hidden className="size-[1em] shrink-0" />}
              {tab.label}
              {current && <span aria-hidden className="absolute inset-x-0 bottom-0 h-[calc(var(--hairline)*2)] bg-primary" />}
            </a>
          )
        })}
      </div>
    </nav>
  )
}

/**
 * One chart or table with its title.
 *
 * Every panel on these pages is either from daily totals (kept forever, fifteen minutes behind)
 * or from the fact log (live, shortened by any retention window). The panels no longer say which
 * on screen; the page code and spec 10.1 do.
 */
export function Panel({
  id,
  title,
  right,
  flush = false,
  children,
}: {
  /** An anchor to jump to; clear of the sticky top bar, like a Section's heading. */
  id?: string
  title: string
  right?: React.ReactNode
  /** Runs the content to the panel's edges, for a table or an `EmptyRow`. */
  flush?: boolean
  children: React.ReactNode
}) {
  return (
    <Card id={id} className={id ? 'scroll-mt-20' : undefined}>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
        {right && <CardAction>{right}</CardAction>}
      </CardHeader>
      <CardContent className={flush ? 'p-0' : undefined}>{children}</CardContent>
    </Card>
  )
}

/**
 * Where a chart will be, while it loads or after it failed: as tall as the chart, so the panel
 * does not jump when the data arrives. A panel with nothing to show at all is `flush` and holds
 * an `EmptyRow`.
 *
 * `danger` when the chart could not be read, so a failure does not look like a chart still loading.
 */
export function Nothing({
  children,
  height,
  tone,
}: {
  children: React.ReactNode
  height: number
  tone?: 'neutral' | 'danger'
}) {
  return (
    <EmptyRow className="px-0" minHeight={height} tone={tone}>
      {children}
    </EmptyRow>
  )
}

/**
 * What a page shows in place of its panels: while it loads, when there is nothing to show, or
 * when it could not be read. `danger` for the last, the same as `Empty` on the list pages.
 */
export function PageMessage({ children, tone }: { children: React.ReactNode; tone?: 'neutral' | 'danger' }) {
  return (
    <Card>
      <EmptyRow tone={tone}>{children}</EmptyRow>
    </Card>
  )
}

/** A small inline toggle, for choosing between two views of one chart. */
export function Toggle<T extends string>({
  value,
  onChange,
  options,
}: {
  value: T
  onChange: (v: T) => void
  options: { value: T; label: string }[]
}) {
  return <SwitchBank size="sm" value={value} onChange={onChange} options={options} />
}

/**
 * The two extents and the daily totals' age, stated separately.
 *
 * Daily totals outlive the facts they came from, so a chart can legitimately cover a longer period
 * than the fact log does. Stating one range for the page would make whichever panel it did not
 * describe quietly wrong. And the daily totals are only as fresh as their last run, which a panel
 * of round numbers does nothing to reveal.
 */
/**
 * What the page's numbers rest on, in one line: "Recording since Aug 13", with the full account
 * (daily totals, the fact log, what is kept) a click away. The full block at the top of a page was
 * three rows of words a moderator had to get past -- "fact log", "daily totals" -- before any answer.
 */
export function CoverageLine({
  coverage,
  generatedAt,
  after,
}: {
  coverage: AnalyticsCoverage
  generatedAt: string
  /** More of the same line after Details, such as what the Discord bot cannot read. */
  after?: React.ReactNode
}) {
  const [open, setOpen] = useState(false)

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-x-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {coverage.factFirstDay ? (
          <span>
            Recording since <span className="font-mono text-foreground">{longDay(coverage.factFirstDay)}</span>
          </span>
        ) : (
          <span>Nothing recorded yet</span>
        )}
        <span aria-hidden>·</span>
        <button
          type="button"
          onClick={() => setOpen((o) => !o)}
          aria-expanded={open}
          className="rounded-sm underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-ring"
        >
          {open ? 'Hide details' : 'Details'}
        </button>
        {after && (
          <>
            <span aria-hidden>·</span>
            {after}
          </>
        )}
      </div>
      {open && <CoverageNote coverage={coverage} generatedAt={generatedAt} />}
    </div>
  )
}

export function CoverageNote({ coverage, generatedAt }: { coverage: AnalyticsCoverage; generatedAt: string }) {
  // Both ends of a span carry the year, or neither does.
  const span = (from: string | null, to: string | null) => {
    const withYear = needsYear(...[from, to].filter((d): d is string => d !== null).map((d) => `${d}T12:00:00Z`))
    const day = (d: string | null) =>
      d ? <span className="font-mono">{longDay(d, withYear)}</span> : 'nothing recorded'
    return (
      <>
        {day(from)} – {day(to)}
      </>
    )
  }
  const kept = (days: number) =>
    days > 0 ? <span className="font-mono">{lengthOfTime(days * 24 * 60)}</span> : 'forever'

  return (
    <Card>
      <CardHeader>
        <CardTitle>Data covered</CardTitle>
      </CardHeader>
      <CardContent>
        <div className="max-w-lg text-foreground">
          <Row
            label="Daily totals cover"
            value={
              <>
                {span(coverage.dailyTotalsFirstDay, coverage.dailyTotalsLastDay)} · last updated{' '}
                <Ago iso={coverage.dailyTotalsUpdatedAt} now={generatedAt} />
              </>
            }
          />
          <Row
            label="The fact log covers"
            value={span(coverage.factFirstDay, coverage.factLastDay)}
          />
          <Row
            label="Facts kept for"
            value={
              coverage.retentionConfigured ? (
                <>
                  moderation {kept(coverage.moderationFactRetentionDays)} · presence{' '}
                  {kept(coverage.presenceFactRetentionDays)}
                </>
              ) : (
                'forever'
              )
            }
          />
        </div>
      </CardContent>
    </Card>
  )
}
