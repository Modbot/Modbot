import { useEffect, useState } from 'react'
import { CartesianGrid, Line, LineChart, Tooltip, XAxis, YAxis } from 'recharts'
import { ChartFrame, chartHeight, chartTheme, compactNumber, rechartsTooltip, seriesColor } from '@/components/charts'
import { Checkbox } from '@/components/ui/checkbox'
import { ApiError, api, type GroupMemberCountSeries, type MemberCountRange } from '@/lib/api'
import { recallLines, rememberLines, switchLine, type MemberCountLines } from './memberCountLines'
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
 *
 * Each line can be hidden, with its axis, so the other line's scale fills the chart: the online
 * count is a few dozen moving under a membership of thousands, and on its own it shows the shape
 * of an evening the shared chart flattens. The tick boxes are the legend too. At least one line
 * stays on, and the choice is remembered in this browser (`memberCountLines`).
 */
export function MemberCountChart() {
  const [range, setRange] = useState<MemberCountRange>('week')
  const [data, setData] = useState<GroupMemberCountSeries | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [lines, setLines] = useState<MemberCountLines>(recallLines)

  const show = (line: keyof MemberCountLines, on: boolean) => {
    const next = switchLine(lines, line, on)
    setLines(next)
    rememberLines(next)
  }

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
  const onlyOne = !(lines.members && lines.online)

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
        <div className="flex flex-col gap-2">
          {rows.length > 0 && (
            <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
              <Checkbox checked={lines.members} disabled={onlyOne && lines.members} onChange={(on) => show('members', on)}>
                <LineName color={seriesColor(1)}>Members</LineName>
              </Checkbox>
              <Checkbox checked={lines.online} disabled={onlyOne && lines.online} onChange={(on) => show('online', on)}>
                <LineName color={chartTheme.ok}>Online</LineName>
              </Checkbox>
            </div>
          )}
          <ChartFrame height={chartHeight.tall} empty={rows.length === 0}>
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
              {lines.members && (
                <YAxis
                  yAxisId="members"
                  width="auto"
                  domain={['auto', 'auto']}
                  tickFormatter={compactNumber}
                  tickLine={false}
                  axisLine={false}
                />
              )}
              {lines.online && (
                <YAxis
                  yAxisId="online"
                  orientation={lines.members ? 'right' : 'left'}
                  width="auto"
                  domain={[0, 'auto']}
                  tickFormatter={compactNumber}
                  tickLine={false}
                  axisLine={false}
                  tick={{ style: { fill: chartTheme.ok } }}
                />
              )}
              <Tooltip
                content={rechartsTooltip((label) => readingTime(Number(label)), names, undefined, { members: 'member' })}
                cursor={{ stroke: 'var(--chart-grid)' }}
              />
              {lines.members && (
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
              )}
              {lines.online && (
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
              )}
            </LineChart>
          </ChartFrame>
        </div>
      )}
    </Panel>
  )
}

/** A line's name beside its tick box, with the line's colour, as the legend drew it. */
function LineName({ color, children }: { color: string; children: React.ReactNode }) {
  return (
    <span className="inline-flex items-center gap-1.5 text-muted-foreground">
      <span className="size-2.5 shrink-0 rounded-full" style={{ background: color }} />
      {children}
    </span>
  )
}
