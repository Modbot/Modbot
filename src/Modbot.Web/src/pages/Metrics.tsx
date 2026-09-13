import { useEffect, useState } from 'react'
import { Card, CardContent } from '@/components/ui/card'
import { ColumnChart, Legend, RankedBars, TimeSeriesChart } from '@/components/charts'
import { compact, denseDays, formatDay } from '@/lib/format'
import { api, ApiError, type Metrics as MetricsData } from '@/lib/api'
import { cn } from '@/lib/utils'

/**
 * Group health over time (spec 5.6, 10.1).
 *
 * Two sources on one screen, kept visibly apart. The daily series come from the daily totals, which are
 * never aged out; the per-type breakdown and the observed headcount come from the fact log, which
 * a retention window can shorten. Showing one date range over both would claim they were the same,
 * so each panel says what it was computed from and the footer states both extents.
 *
 * The series that is *not* here is as deliberate as the ones that are: `members.net` is a
 * running net of recorded joins and leaves from zero, so it is shown as "net change since Modbot
 * started recording" and never as a headcount. The headcount comes from the group-info sync,
 * which is a number VRChat actually reported.
 */

const WINDOWS = [
  { days: 30, label: '30 days' },
  { days: 90, label: '90 days' },
  { days: 365, label: '1 year' },
]

export function Metrics() {
  const [days, setDays] = useState(90)
  const [data, setData] = useState<MetricsData | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    api
      .metrics(days)
      .then((next) => {
        if (!cancelled) {
          setData(next)
          setError(null)
        }
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to read analytics.'
            : 'Could not load metrics.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [days])

  if (error) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">{error}</CardContent>
      </Card>
    )
  }

  if (!data) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">Loading…</CardContent>
      </Card>
    )
  }

  const series = (metric: string) => data.series.find((s) => s.metric === metric)
  const total = (metric: string) =>
    series(metric)?.points.reduce((sum, p) => sum + p.value, 0) ?? 0

  const memberCount = denseDays(data.from, data.to, data.memberCount, 'carry')
  const latestCount = memberCount[memberCount.length - 1]

  const anyData =
    data.series.some((s) => s.points.length > 0) ||
    data.memberCount.length > 0 ||
    data.actionsByType.some((s) => s.total > 0)

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2">
        {WINDOWS.map((w) => (
          <button
            key={w.days}
            type="button"
            aria-pressed={days === w.days}
            onClick={() => setDays(w.days)}
            className={cn(
              'rounded-md border px-3 font-medium transition-colors',
              days === w.days
                ? 'border-transparent bg-accent text-accent-foreground'
                : 'text-muted-foreground hover:text-foreground',
            )}
            style={{
              fontSize: 'var(--text-small)',
              borderWidth: 'var(--hairline)',
              height: 'var(--control-h)',
            }}
          >
            {w.label}
          </button>
        ))}
        <span className="flex-1" />
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {formatDay(`${data.from}T00:00:00Z`)} – {formatDay(`${data.to}T00:00:00Z`)}
        </span>
      </div>

      {!anyData && (
        <Card>
          <CardContent className="py-10 text-center text-muted-foreground">
            <div className="font-medium text-foreground">Nothing recorded in this window</div>
            <p className="mx-auto mt-1 max-w-md" style={{ fontSize: 'var(--text-small)' }}>
              Either nothing has happened in the group, or Modbot has not been syncing long enough
              to have seen it. The coverage note at the bottom says which.
            </p>
          </CardContent>
        </Card>
      )}

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Stat
          label="Members (observed)"
          value={latestCount ? compact(latestCount.value) : '—'}
          note={
            latestCount
              ? `as of ${formatDay(`${latestCount.day}T00:00:00Z`)}`
              : 'group-info sync has not recorded a headcount yet'
          }
        />
        <Stat label="Joined" value={compact(total('members.joined'))} note="recorded in this window" />
        <Stat label="Left" value={compact(total('members.left'))} note="recorded in this window" />
        <Stat label="Bans" value={compact(total('bans.added'))} note="recorded in this window" />
      </div>

      <Panel
        title="Member count"
        source="From group-info facts — the headcount VRChat reported"
        note={
          data.memberCount.length === 0
            ? 'Nothing yet. The group-info sync writes a fact only when something actually changed, so this fills in once a change is observed.'
            : 'Held flat between observations, because the sync records a change rather than a daily reading.'
        }
      >
        <TimeSeriesChart points={memberCount} series={1} valueLabel="members" zeroBased={false} />
      </Panel>

      <div className="grid gap-4 lg:grid-cols-2">
        <Panel title="Joins and leaves" source="From daily totals" note={null}>
          <Legend
            items={[
              { label: 'Joined', series: 3 },
              { label: 'Left', series: 2 },
            ]}
          />
          <div className="mt-2 flex flex-col gap-3">
            <ColumnChart
              points={denseDays(data.from, data.to, series('members.joined')?.points ?? [], 'zero')}
              series={3}
              valueLabel="joined"
              height={104}
            />
            <ColumnChart
              points={denseDays(data.from, data.to, series('members.left')?.points ?? [], 'zero')}
              series={2}
              valueLabel="left"
              height={104}
            />
          </div>
        </Panel>

        <Panel
          title="Moderator activity"
          source="From daily totals, dimensioned by actor"
          note={
            'VRChat attributes everything Modbot itself does to Modbot’s own account, so once ' +
            'Modbot performs actions these totals will show that account rather than the person ' +
            'behind it. Today every action here was performed in VRChat directly.'
          }
        >
          {data.moderators.length === 0 ? (
            <Nothing>No moderator actions recorded in this window.</Nothing>
          ) : (
            <RankedBars
              series={1}
              rows={data.moderators.map((m) => ({
                key: m.dimension,
                label: m.name ?? m.actorId,
                value: m.actions,
              }))}
            />
          )}
        </Panel>
      </div>

      <Panel
        title="Moderation actions by type"
        source="Counted from the fact log, not from daily totals"
        note={
          'No daily total carries a per-type daily breakdown, so this one is bounded by the facts that ' +
          'survive retention rather than by the daily totals. Small multiples rather than one stacked ' +
          'chart: five series on one axis at this scale is unreadable, and these are compared ' +
          'against their own history far more often than against each other.'
        }
      >
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
          {data.actionsByType.map((s, i) => (
            <div key={s.type}>
              <div className="flex items-baseline gap-2">
                <span
                  className="size-2.5 shrink-0 rounded-full"
                  style={{ background: `var(--series-${((i % 5) + 1) as 1 | 2 | 3 | 4 | 5})` }}
                />
                <span className="font-medium">{s.label}</span>
                <span className="flex-1" />
                <span className="tabular-nums text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                  {compact(s.total)}
                </span>
              </div>
              <ColumnChart
                points={denseDays(data.from, data.to, s.points, 'zero')}
                series={((i % 5) + 1) as 1 | 2 | 3 | 4 | 5}
                valueLabel={s.label.toLowerCase()}
                height={96}
              />
            </div>
          ))}
        </div>
      </Panel>

      <Panel
        title={series('members.net')?.label ?? 'Net change'}
        source="From daily totals"
        note={series('members.net')?.note ?? null}
      >
        <TimeSeriesChart
          points={denseDays(data.from, data.to, series('members.net')?.points ?? [], 'carry')}
          series={4}
          valueLabel="net"
        />
      </Panel>

      <Coverage data={data} />
    </div>
  )
}

function Stat({ label, value, note }: { label: string; value: string; note: string }) {
  return (
    <Card>
      <CardContent className="py-3">
        <div
          className="font-semibold uppercase tracking-wider text-muted-foreground"
          style={{ fontSize: 'var(--text-small)' }}
        >
          {label}
        </div>
        <div className="mt-1 font-mono text-2xl font-medium tracking-tight">{value}</div>
        <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {note}
        </div>
      </CardContent>
    </Card>
  )
}

function Panel({
  title,
  source,
  note,
  children,
}: {
  title: string
  source: string
  note: string | null
  children: React.ReactNode
}) {
  return (
    <Card>
      <CardContent className="py-4">
        <div className="mb-1 flex flex-wrap items-baseline gap-x-3">
          <span className="font-medium">{title}</span>
          <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {source}
          </span>
        </div>
        {note && (
          <p className="mb-3 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {note}
          </p>
        )}
        {children}
      </CardContent>
    </Card>
  )
}

function Nothing({ children }: { children: React.ReactNode }) {
  return (
    <div
      className="grid text-muted-foreground"
      style={{ height: 132, placeItems: 'center', fontSize: 'var(--text-small)' }}
    >
      {children}
    </div>
  )
}

/**
 * The two extents, stated separately.
 *
 * Daily totals outlive the facts they came from, so a chart can legitimately cover a longer period
 * than the audit log does. Stating one range for the screen would make whichever panel it did not
 * describe quietly wrong.
 */
function Coverage({ data }: { data: MetricsData }) {
  const day = (d: string | null) => (d ? formatDay(`${d}T00:00:00Z`) : 'nothing recorded')

  return (
    <Card>
      <CardContent className="py-4">
        <div className="mb-2 font-medium">What these charts are built from</div>
        <dl className="flex flex-col gap-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          <div className="flex flex-wrap gap-1.5">
            <dt>Daily totals cover</dt>
            <dd className="font-medium text-foreground">
              {day(data.coverage.dailyTotalsFirstDay)} – {day(data.coverage.dailyTotalsLastDay)}
            </dd>
          </div>
          <div className="flex flex-wrap gap-1.5">
            <dt>The fact log covers</dt>
            <dd className="font-medium text-foreground">
              {day(data.coverage.factFirstDay)} – {day(data.coverage.factLastDay)}
            </dd>
          </div>
        </dl>
        <p className="mt-2 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {data.coverage.retentionConfigured
            ? `A retention window is configured (moderation ${data.coverage.moderationFactRetentionDays} days, presence ${data.coverage.presenceFactRetentionDays} days), so facts older than it have been destroyed. Daily totals are never aged out and keep their full history — which is why the two ranges above differ.`
            : 'Nothing is being deleted: both retention windows are set to keep forever. The two ranges can still differ, because the audit-log catch-up walks history backwards while the daily totals job only folds forward from what it has seen.'}
        </p>
      </CardContent>
    </Card>
  )
}
