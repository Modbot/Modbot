import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { longDay } from '@/components/charts'
import { ago } from '@/lib/format'
import type { AnalyticsCoverage } from '@/lib/api'
import { SwitchBank } from '@/components/ui/switch-bank'
import { cn } from '@/lib/utils'
import { RANGES, type Range } from './useAnalytics'

/**
 * What the four Analytics pages share: the date-range control, the panel and stat frames, the
 * loading and error states, and the footer that says where the numbers came from.
 *
 * Kept as plain building blocks rather than a page template, because the pages are meant to be
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
          {longDay(from)} – {longDay(to)}
        </span>
      )}
    </div>
  )
}

/**
 * One reading, laid out like a gauge on a panel: its name in the top-left corner, the number in
 * the bottom-right, and any note beside the number on the left, so the numbers of a row share
 * one baseline whether or not they carry a note. On its own it draws its own edge; inside a
 * StatStrip it shares its edges with the others.
 */
export function Stat({ label, value, note }: { label: string; value: string; note?: string }) {
  return (
    <div
      data-slot="stat"
      className="flex min-w-0 flex-col border border-(length:--hairline) bg-card px-(--panel-pad) pt-2 pb-2.5"
      style={{ minHeight: 'calc(var(--row-h) * 2)' }}
    >
      <div className="truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {label}
      </div>
      <div className="mt-auto flex items-end gap-2 pt-3">
        {note && (
          <div className="min-w-0 truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {note}
          </div>
        )}
        <div
          className="ml-auto shrink-0 font-mono leading-none font-medium tracking-tight"
          style={{ fontSize: 'calc(var(--text-base) * 1.75)' }}
        >
          {value}
        </div>
      </div>
    </div>
  )
}

/** A row of Stats sharing one hairline grid. Two across on a phone, the caller's count when wider. */
export function StatStrip({ className, children }: { className?: string; children: React.ReactNode }) {
  return <PanelGrid className={cn('grid-cols-2 xl:grid-cols-4', className)}>{children}</PanelGrid>
}

/**
 * One chart or table with its title.
 *
 * Every panel on these pages is either from daily totals (kept forever, fifteen minutes behind)
 * or from the fact log (live, shortened by any retention window). The panels no longer say which
 * on screen; the page code and spec 10.1 do.
 */
export function Panel({
  title,
  right,
  flush = false,
  children,
}: {
  title: string
  right?: React.ReactNode
  /** Runs the content to the panel's edges, for a table. */
  flush?: boolean
  children: React.ReactNode
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
        {right && <span className="ml-auto">{right}</span>}
      </CardHeader>
      <CardContent className={flush ? 'p-0' : undefined}>{children}</CardContent>
    </Card>
  )
}

/**
 * A table run to its panel's edges (pass `flush` to the Panel): the column names on the strip,
 * one hairline between rows. `pinFirst` keeps the first column in place while a phone scrolls the
 * rest sideways, for tables whose first column names the row.
 */
export function Table({
  head,
  pinFirst = false,
  children,
}: {
  head: React.ReactNode
  pinFirst?: boolean
  children: React.ReactNode
}) {
  return (
    <div data-pin-first={pinFirst || undefined} className="relative overflow-x-auto">
      <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
        <thead className="bg-strip text-left text-muted-foreground">
          <tr>{head}</tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  )
}

export function Th({ className, ...props }: React.ComponentProps<'th'>) {
  return <th className={cn('px-(--panel-pad) py-1.5 font-normal whitespace-nowrap', className)} {...props} />
}

export function Tr({ className, ...props }: React.ComponentProps<'tr'>) {
  return <tr className={cn('h-(--row-h) border-t border-t-(length:--hairline)', className)} {...props} />
}

export function Td({ className, ...props }: React.ComponentProps<'td'>) {
  return <td className={cn('px-(--panel-pad) py-1', className)} {...props} />
}

export function Nothing({ children, height }: { children: React.ReactNode; height?: number }) {
  return (
    <EmptyRow className="px-0" minHeight={height}>
      {children}
    </EmptyRow>
  )
}

export function PageMessage({ children }: { children: React.ReactNode }) {
  return (
    <Card>
      <EmptyRow>{children}</EmptyRow>
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
export function CoverageNote({ coverage, generatedAt }: { coverage: AnalyticsCoverage; generatedAt: string }) {
  const day = (d: string | null) => (d ? <span className="font-mono">{longDay(d)}</span> : 'nothing recorded')
  const kept = (days: number) => (days > 0 ? `${days} days` : 'forever')

  return (
    <Card>
      <CardHeader>
        <CardTitle>Data covered</CardTitle>
      </CardHeader>
      <CardContent>
        <dl className="flex flex-col gap-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          <div className="flex flex-wrap gap-1.5">
            <dt>Daily totals cover</dt>
            <dd className="font-medium text-foreground">
              {day(coverage.dailyTotalsFirstDay)} – {day(coverage.dailyTotalsLastDay)}
            </dd>
            <dd>
              · last updated{' '}
              <span className="font-medium text-foreground">
                {coverage.dailyTotalsUpdatedAt ? ago(coverage.dailyTotalsUpdatedAt, generatedAt) : 'never'}
              </span>
            </dd>
          </div>
          <div className="flex flex-wrap gap-1.5">
            <dt>The fact log covers</dt>
            <dd className="font-medium text-foreground">
              {day(coverage.factFirstDay)} – {day(coverage.factLastDay)}
            </dd>
          </div>
          <div className="flex flex-wrap gap-1.5">
            <dt>Facts kept for</dt>
            <dd className="font-medium text-foreground">
              {coverage.retentionConfigured
                ? `moderation ${kept(coverage.moderationFactRetentionDays)} · presence ${kept(coverage.presenceFactRetentionDays)}`
                : 'forever'}
            </dd>
          </div>
        </dl>
      </CardContent>
    </Card>
  )
}
