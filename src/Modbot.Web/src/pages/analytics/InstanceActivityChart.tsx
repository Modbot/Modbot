import { useEffect, useState } from 'react'
import { CartesianGrid, Line, LineChart, Tooltip, XAxis, YAxis } from 'recharts'
import { ChartFrame, chartHeight, chartTheme, compactNumber, rechartsTooltip, seriesColor } from '@/components/charts'
import { ApiError, api, type InstanceActivitySeries, type MemberCountRange } from '@/lib/api'
import { toActivityRows } from './instanceActivitySeries'
import { MEMBER_COUNT_RANGES, readingTime, timeLabel, timeTicks } from './memberCountSeries'
import { Nothing, Panel, Toggle } from './shared'

/**
 * People in the group's instances, moment by moment.
 *
 * Its own range, separate from the page's, for the same reason the member count chart has one: the
 * page's ranges are whole days of daily totals and this is a staircase of readings taken every
 * thirty seconds. The server thins a long range to about 500 points, each of them a total that was
 * true at the time shown.
 *
 * Two axes on one chart, against the charts' one-axis rule and for the same reason the member count
 * chart breaks it: the two lines are one thing at two magnitudes -- the same people, and how many
 * instances they were spread across -- and thirty people in one instance is a different evening
 * from thirty across six. The instance axis is on the right, in the instance line's colour, so a
 * value can only be read against the axis it belongs to.
 *
 * `step` is a staircase and not a curve, because a head count is kept only when it changes and the
 * value between two readings is the earlier one's, right up to the next.
 */
export function InstanceActivityChart() {
  const [range, setRange] = useState<MemberCountRange>('week')
  const [data, setData] = useState<InstanceActivitySeries | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    api
      .instanceActivity(range)
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
            : 'Could not load instance activity.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [range])

  const rows = data ? toActivityRows(data) : []
  const from = data ? Date.parse(data.from) : 0
  const to = data ? Date.parse(data.to) : 0
  const span = to - from
  const names = { people: 'people', instances: 'instances' }

  return (
    <Panel
      title="People in instances"
      right={
        <Toggle
          value={range}
          onChange={setRange}
          options={MEMBER_COUNT_RANGES.map((r) => ({ value: r.range, label: r.label }))}
        />
      }
    >
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="flex items-center gap-1.5 text-muted-foreground">
          <span className="size-2.5 shrink-0 rounded-full" style={{ background: seriesColor(1) }} />
          People
        </span>
        <span className="flex items-center gap-1.5 text-muted-foreground">
          <span className="size-2.5 shrink-0 rounded-full" style={{ background: chartTheme.ok }} />
          Instances
        </span>
      </div>

      <div className="mt-2">
        {error ? (
          <Nothing height={chartHeight.tall}>{error}</Nothing>
        ) : !data ? (
          <Nothing height={chartHeight.tall}>Loading…</Nothing>
        ) : (
          <ChartFrame height={chartHeight.tall} empty={rows.length === 0} emptyText="No head counts in this range.">
            <LineChart data={rows} margin={{ top: 6, right: 8, bottom: 0, left: 0 }}>
              <CartesianGrid vertical={false} />
              <XAxis
                dataKey="at"
                type="number"
                domain={[from, to]}
                ticks={timeTicks(from, to)}
                tickFormatter={(v: number) => timeLabel(v, span)}
                tickLine={false}
                axisLine={false}
                minTickGap={16}
              />
              <YAxis
                yAxisId="people"
                width="auto"
                domain={[0, 'auto']}
                tickFormatter={compactNumber}
                tickLine={false}
                axisLine={false}
              />
              <YAxis
                yAxisId="instances"
                orientation="right"
                width="auto"
                domain={[0, 'auto']}
                allowDecimals={false}
                tickFormatter={compactNumber}
                tickLine={false}
                axisLine={false}
                tick={{ style: { fill: chartTheme.ok } }}
              />
              <Tooltip
                content={rechartsTooltip((label) => readingTime(Number(label)), names)}
                cursor={{ stroke: 'var(--chart-grid)' }}
              />
              <Line
                yAxisId="people"
                type="stepAfter"
                dataKey="people"
                name="people"
                stroke={seriesColor(1)}
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4, strokeWidth: 2, stroke: 'var(--card)' }}
                isAnimationActive={false}
              />
              <Line
                yAxisId="instances"
                type="stepAfter"
                dataKey="instances"
                name="instances"
                stroke={chartTheme.ok}
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4, strokeWidth: 2, stroke: 'var(--card)' }}
                isAnimationActive={false}
              />
            </LineChart>
          </ChartFrame>
        )}
      </div>
    </Panel>
  )
}
