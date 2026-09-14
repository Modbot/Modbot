import { Card, CardContent } from '@/components/ui/card'
import { longDay } from '@/components/charts'
import { ago } from '@/lib/format'
import type { AnalyticsCoverage } from '@/lib/api'
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
      {RANGES.map((r) => (
        <button
          key={String(r.range)}
          type="button"
          aria-pressed={range === r.range}
          onClick={() => onChange(r.range)}
          className={cn(
            'rounded-md border px-3 font-medium transition-colors',
            range === r.range
              ? 'border-transparent bg-accent text-accent-foreground'
              : 'text-muted-foreground hover:text-foreground',
          )}
          style={{ fontSize: 'var(--text-small)', borderWidth: 'var(--hairline)', height: 'var(--control-h)' }}
        >
          {r.label}
        </button>
      ))}
      <span className="flex-1" />
      {from && to && (
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {longDay(from)} – {longDay(to)}
        </span>
      )}
    </div>
  )
}

export function Stat({ label, value, note }: { label: string; value: string; note?: string }) {
  return (
    <Card>
      <CardContent className="py-3">
        <div className="font-semibold uppercase tracking-wider text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {label}
        </div>
        <div className="mt-1 font-mono text-2xl font-medium tracking-tight">{value}</div>
        {note && (
          <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {note}
          </div>
        )}
      </CardContent>
    </Card>
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
  title,
  right,
  children,
}: {
  title: string
  right?: React.ReactNode
  children: React.ReactNode
}) {
  return (
    <Card>
      <CardContent className="py-4">
        <div className="mb-3 flex flex-wrap items-baseline gap-x-3">
          <span className="font-medium">{title}</span>
          {right && <span className="ml-auto">{right}</span>}
        </div>
        {children}
      </CardContent>
    </Card>
  )
}

export function Nothing({ children, height = 132 }: { children: React.ReactNode; height?: number }) {
  return (
    <div className="grid text-muted-foreground" style={{ height, placeItems: 'center', fontSize: 'var(--text-small)' }}>
      <p className="max-w-md text-center">{children}</p>
    </div>
  )
}

export function PageMessage({ children }: { children: React.ReactNode }) {
  return (
    <Card>
      <CardContent className="py-10 text-center text-muted-foreground">{children}</CardContent>
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
  return (
    <div role="group" className="flex gap-0.5 rounded-md border bg-secondary p-0.5" style={{ borderWidth: 'var(--hairline)' }}>
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          aria-pressed={value === o.value}
          onClick={() => onChange(o.value)}
          className={cn(
            'rounded px-2 font-medium transition-colors',
            value === o.value ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
          )}
          style={{ fontSize: 'var(--text-small)', height: 'calc(var(--control-h) - 8px)' }}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
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
  const day = (d: string | null) => (d ? longDay(d) : 'nothing recorded')
  const kept = (days: number) => (days > 0 ? `${days} days` : 'forever')

  return (
    <Card>
      <CardContent className="py-4">
        <div className="mb-2 font-medium">Data covered</div>
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
