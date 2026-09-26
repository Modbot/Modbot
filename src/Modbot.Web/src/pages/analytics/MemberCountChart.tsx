import { useEffect, useState } from 'react'
import { CartesianGrid, Line, LineChart, Tooltip, XAxis, YAxis } from 'recharts'
import { ChartFrame, chartHeight, chartTheme, compactNumber, rechartsTooltip, seriesColor } from '@/components/charts'
import { ApiError, api, type GroupMemberCountSeries, type MemberCountRange } from '@/lib/api'
import { MEMBER_COUNT_RANGES, readingTime, timeLabel, timeTicks, toRows } from './memberCountSeries'
import { Nothing, Panel, Toggle } from './shared'

/**
 * The group's member count and online member count, reading by reading.
 *
 * Its own range, separate from the page's: the page's ranges are whole days of daily totals,
 * and this chart is five-minute readings, where a day is the interesting range and a week is
 * already two thousand points. The server thins a long range to about 500 points, each a real
 * reading (see `GroupMemberCountQuery`), so the chart never decides which readings to drop.
 *
 * Two axes on one chart, against the charts' one-axis rule, because the two lines are one thing
 * -- the same people, in the group and online now -- at two magnitudes, and the question the
 * overlay answers is how the second moves against the first. The online axis is on the right, in
 * the online line's colour, so a value can only be read against the axis it belongs to.
 */
export function MemberCountChart() {
  const [range, setRange] = useState<MemberCountRange>('week')
  const [data, setData] = useState<GroupMemberCountSeries | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    api
      .groupMemberCount(range)
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
            : 'Could not load the member count.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [range])

  const rows = data ? toRows(data.points) : []
  const from = data ? Date.parse(data.from) : 0
  const to = data ? Date.parse(data.to) : 0
  const span = to - from
  const names = { members: 'members', online: 'online' }

  return (
    <Panel
      title="Member count"
      right={
        <Toggle
          value={range}
          onChange={setRange}
          options={MEMBER_COUNT_RANGES.map((r) => ({ value: r.range, label: r.label }))}
        />
      }
    >
      {error ? (
        <Nothing height={chartHeight.tall} tone="danger">{error}</Nothing>
      ) : !data ? (
        <Nothing height={chartHeight.tall}>Loading…</Nothing>
      ) : (
        <ChartFrame
          height={chartHeight.tall}
          empty={rows.length === 0}
          legend={[
            { label: 'Members', slot: 1 },
            { label: 'Online', color: chartTheme.ok },
          ]}
        >
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
              yAxisId="members"
              width="auto"
              domain={['auto', 'auto']}
              tickFormatter={compactNumber}
              tickLine={false}
              axisLine={false}
            />
            <YAxis
              yAxisId="online"
              orientation="right"
              width="auto"
              domain={[0, 'auto']}
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
              yAxisId="members"
              type="monotone"
              dataKey="members"
              name="members"
              stroke={seriesColor(1)}
              strokeWidth={2}
              dot={false}
              activeDot={{ r: 4, strokeWidth: 2, stroke: 'var(--card)' }}
              isAnimationActive={false}
            />
            <Line
              yAxisId="online"
              type="monotone"
              dataKey="online"
              name="online"
              stroke={chartTheme.ok}
              strokeWidth={2}
              dot={false}
              activeDot={{ r: 4, strokeWidth: 2, stroke: 'var(--card)' }}
              isAnimationActive={false}
            />
          </LineChart>
        </ChartFrame>
      )}
    </Panel>
  )
}
