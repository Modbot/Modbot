import { useEffect, useState } from 'react'
import { CartesianGrid, Line, LineChart, ReferenceArea, Tooltip, XAxis, YAxis, type TooltipContentProps } from 'recharts'
import {
  ChartFrame,
  ChartTooltip,
  MissingBands,
  Stripes,
  chartHeight,
  chartTheme,
  compactNumber,
  seriesColor,
  useStripeId,
} from '@/components/charts'
import { ApiError, api, type InstanceActivitySeries, type MemberCountRange } from '@/lib/api'
import { plural } from '@/lib/format'
import { activityChartRows, highest, openStretches, wholeTicks, type ActivityChartRow } from './instanceActivitySeries'
import { MEMBER_COUNT_RANGES, readingTime, timeLabel, timeTicks } from './memberCountSeries'
import { Nothing, Panel, Toggle } from './shared'

/** How strongly an open stretch is shaded: light for one instance, darker for two or more. */
const OPEN_OPACITY = { one: 0.13, twoOrMore: 0.3 }

/**
 * People in the group's instances, moment by moment.
 *
 * Its own range, separate from the page's, for the same reason the member count chart has one: the
 * page's ranges are whole days of daily totals and this is a staircase of readings taken every
 * thirty seconds. The server thins a long range to about 500 steps, keeping each step's highest
 * reading and its last, so the line reaches every peak and each point is a total that was true at
 * the time shown.
 *
 * One scale, people. How many instances were open is shading behind the line rather than a second
 * line on a second axis: with two axes, "1 instance" sat exactly where "30 people" did and read as
 * thirty. The shading needs no scale -- light where one instance was open, darker where two or more
 * were -- and the tooltip gives the exact count. The shading is drawn first, the stripes of missing
 * days over it, and the two never meet anyway: a break in the line is never shaded
 * (`openStretches`).
 *
 * The scale counts whole people from nought and ends on the tick just above the highest value
 * (`wholeTicks`): no half people.
 *
 * `step` is a staircase and not a curve, because a head count is kept only when it changes and the
 * value between two readings is the earlier one's, right up to the next. A day an instance was open
 * and never counted breaks the staircase and sits in a striped band.
 */
export function InstanceActivityChart() {
  const [range, setRange] = useState<MemberCountRange>('week')
  const [data, setData] = useState<InstanceActivitySeries | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tries, setTries] = useState(0)

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
  }, [range, tries])

  const stripeId = useStripeId()
  const chart = data ? activityChartRows(data) : { rows: [], bands: [] }
  const rows = chart.rows
  const open = openStretches(rows)
  const from = data ? Date.parse(data.from) : 0
  const to = data ? Date.parse(data.to) : 0
  const span = to - from
  const peopleTicks = wholeTicks(highest(rows, 'people'))

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
      {error ? (
        <Nothing
          height={chartHeight.tall}
          tone="danger"
          onTryAgain={() => {
            setError(null)
            setTries((n) => n + 1)
          }}
        >
          {error}
        </Nothing>
      ) : !data ? (
        <Nothing height={chartHeight.tall} tone="loading" />
      ) : (
        <ChartFrame
          height={chartHeight.tall}
          empty={!data || data.points.length === 0}
          emptyText="No head counts in this range."
          legend={[
            { label: 'People', slot: 1, sample: 'line' },
            { label: 'Instance open (darker: 2+)', color: chartTheme.ok, sample: 'band' },
          ]}
        >
          <LineChart data={rows} margin={{ top: 6, right: 8, bottom: 0, left: 0 }}>
            <Stripes id={stripeId} />
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
              width="auto"
              domain={[0, peopleTicks[peopleTicks.length - 1]]}
              ticks={peopleTicks}
              allowDecimals={false}
              tickFormatter={compactNumber}
              tickLine={false}
              axisLine={false}
            />
            {open.map((s) => (
              <ReferenceArea
                key={`${s.x1}-${s.x2}`}
                x1={s.x1}
                x2={s.x2}
                fill={chartTheme.ok}
                fillOpacity={s.twoOrMore ? OPEN_OPACITY.twoOrMore : OPEN_OPACITY.one}
                stroke="none"
                ifOverflow="hidden"
              />
            ))}
            <MissingBands stripeId={stripeId} bands={chart.bands} />
            <Tooltip content={ReadingTooltip} cursor={{ stroke: 'var(--chart-grid)' }} />
            <Line
              type="stepAfter"
              dataKey="people"
              name="people"
              stroke={seriesColor(1)}
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

/** The reading under the pointer, both counts on one line: "46 people · 1 instance open". */
function ReadingTooltip({ active, label, payload }: TooltipContentProps) {
  const row = payload?.[0]?.payload as ActivityChartRow | undefined
  if (!active || !row || row.people === null) return null

  const instances = row.instances ?? 0

  return (
    <ChartTooltip
      title={readingTime(Number(label))}
      rows={[
        {
          value: compactNumber(row.people),
          name: `${plural(row.people, 'person', 'people')} · ${instances} ${plural(instances, 'instance', 'instances')} open`,
          color: payload?.[0]?.color,
        },
      ]}
    />
  )
}
