import { useCallback, useEffect, useState } from 'react'
import { CartesianGrid, Line, LineChart, Tooltip, XAxis, YAxis, type TooltipContentProps } from 'recharts'
import { ChartFrame, ChartTooltip, chartHeight, compactNumber, minutes, seriesColor } from '@/components/charts'
import { EmptyRow } from '@/components/PanelGrid'
import { Panel } from '@/components/subject/shared'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Stat, StatStrip } from '@/pages/analytics/shared'
import { readingTime, timeLabel, timeTicks } from '@/pages/analytics/memberCountSeries'
import { useGroupInfo } from '@/lib/useGroupInfo'
import { useLoad } from '@/lib/useLoad'
import { api, type InstanceWorldView, type OtherInstance, type WorldReading } from '@/lib/api'
import { accessInGame, headCountText, ordinal } from '@/lib/format'
import { instanceNumber } from '@/lib/instanceName'
import { openInstance } from '@/lib/subject'

/** The other instances' lines: one quiet colour, so this instance is the one line that stands out. */
const OTHER_STROKE = 'var(--muted-foreground)'

/**
 * While VRChat is being asked for the others' names, the tab asks again this often, this many times:
 * about half a minute, enough for a popup's eight instances at one read a second and a few groups.
 * Anything later shows the next time the tab opens.
 */
const NAMES_AGAIN_AFTER_MS = 8000
const NAMES_AGAIN_TIMES = 4

/**
 * How this instance compared with the other instances in its world while it was open: its head count
 * against the busiest of theirs at each read of the world's page, where it ranked, and how many were
 * in the world.
 *
 * The world's page is read every two minutes while the group has an instance open in it, so an
 * instance from before those reads began has none, and the panel says only that. Loaded on its own
 * rather than with the instance, so the rest of the popup never waits on it.
 */
export function InstanceWorld({ id, live }: { id: string; live: number }) {
  const load = useCallback(() => api.instanceWorld(id), [id])
  const [again, setAgain] = useState(0)
  const { data, error } = useLoad(load, live + again)

  // The answer never waits on VRChat, so names asked for just now arrive on a later answer.
  useEffect(() => {
    if (!data?.namesComing || again >= NAMES_AGAIN_TIMES) return
    const timer = window.setTimeout(() => setAgain((n) => n + 1), NAMES_AGAIN_AFTER_MS)
    return () => window.clearTimeout(timer)
  }, [data, again])

  if (error) {
    return (
      <Panel title="Other instances in this world" flush>
        <EmptyRow>{error}</EmptyRow>
      </Panel>
    )
  }

  if (!data) return null

  if (data.readings.length === 0) {
    return (
      <Panel title="Other instances in this world" flush>
        <EmptyRow>No readings of this world.</EmptyRow>
      </Panel>
    )
  }

  const peak = data.atPeak

  return (
    <>
      <StatStrip className="m-0 shrink-0">
        <Stat label="At its peak" value={peak?.rank ? `${ordinal(peak.rank)} of ${peak.of}` : '—'} />
        <Stat label="Busiest for" value={data.busiestMinutes > 0 ? minutes(data.busiestMinutes) : '—'} />
        <Stat
          label="In the world at its peak"
          value={peak?.occupants == null ? '—' : compactNumber(peak.occupants)}
        />
        <Stat label="Other instances" value={compactNumber(data.othersTotal)} />
      </StatStrip>

      <Panel title="Other instances in this world">
        <WorldChart view={data} />
      </Panel>

      <Panel title="Busiest others" flush>
        <OthersTable view={data} />
      </Panel>
    </>
  )
}

type Row = { at: number; me: number | null; reading: WorldReading } & Record<string, number | null | WorldReading>

/**
 * This instance and the busiest others, one line each, at the world's reads. Staircases, like
 * "People over time": a count holds from one read until the next. An instance missing from a read
 * has no point there, so its line stops rather than drawing a count nobody read.
 */
function WorldChart({ view }: { view: InstanceWorldView }) {
  const from = Date.parse(view.from)
  const to = Date.parse(view.to)
  const span = to - from

  const byTime = new Map<string, Record<string, number>>()
  view.others.forEach((other, i) => {
    for (const r of other.readings) {
      const at = byTime.get(r.at) ?? {}
      at[`o${i}`] = r.people
      byTime.set(r.at, at)
    }
  })

  const rows: Row[] = view.readings.map((reading) => ({
    at: Date.parse(reading.at),
    me: reading.people,
    reading,
    ...byTime.get(reading.at),
  }))

  return (
    <ChartFrame height={chartHeight.tall} empty={rows.length === 0} emptyText="No readings of this world.">
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
        <YAxis width="auto" domain={[0, 'auto']} allowDecimals={false} tickLine={false} axisLine={false} />
        <Tooltip content={(props) => <ReadingTooltip {...props} others={view.others} />} cursor={{ stroke: 'var(--chart-grid)' }} />
        {view.others.map((other, i) => (
          <Line
            key={other.instanceId}
            type="stepAfter"
            dataKey={`o${i}`}
            name={otherLabel(other)}
            stroke={OTHER_STROKE}
            strokeOpacity={0.55}
            strokeWidth={1.25}
            dot={false}
            activeDot={false}
            isAnimationActive={false}
          />
        ))}
        <Line
          type="stepAfter"
          dataKey="me"
          name="This instance"
          stroke={seriesColor(1)}
          strokeWidth={2.5}
          dot={false}
          activeDot={{ r: 4, strokeWidth: 2, stroke: 'var(--card)' }}
          isAnimationActive={false}
        />
      </LineChart>
    </ChartFrame>
  )
}

/** One read under the pointer: this instance and where it stood, then the others shown, busiest first. */
function ReadingTooltip({ active, payload, others }: TooltipContentProps & { others: OtherInstance[] }) {
  const row = payload?.[0]?.payload as Row | undefined
  if (!active || !row) return null

  const reading = row.reading
  const theirs = others
    .map((other, i) => ({ name: otherLabel(other), value: row[`o${i}`] as number | undefined }))
    .filter((o): o is { name: string; value: number } => typeof o.value === 'number')
    .sort((a, b) => b.value - a.value)
    .slice(0, 5)

  return (
    <ChartTooltip
      title={readingTime(row.at)}
      rows={[
        ...(reading.people === null
          ? []
          : [
              {
                name: reading.rank ? `This instance, ${ordinal(reading.rank)} of ${reading.of}` : 'This instance',
                value: headCountText(reading.people, reading.unsure),
                color: seriesColor(1),
              },
            ]),
        ...theirs.map((o) => ({ ...o, color: OTHER_STROKE })),
        ...(reading.occupants === null ? [] : [{ name: 'In the world', value: reading.occupants }]),
      ]}
    />
  )
}

/**
 * The busiest others, each called by the name it was opened with when Modbot knows it, and by its
 * number otherwise. A named one keeps its number under the name, because the number is what finds
 * it in game. One of the group's own says the group's name after it; the group is read once for
 * the whole table, and "your group" stands in until it arrives. Another group's says that group's
 * name once VRChat has been asked for it.
 */
function OthersTable({ view }: { view: InstanceWorldView }) {
  const { info } = useGroupInfo()
  const ownGroup = info?.name?.trim() || 'your group'

  if (view.others.length === 0) return <EmptyRow>No other instances seen.</EmptyRow>

  // A public instance's id carries no qualifier that says who can join, so the column is left out
  // when none of the shown instances says, rather than filled with dashes.
  const saysWhoCanJoin = view.others.some((o) => o.groupId !== null)

  return (
    <Table
      head={
        <>
          <Th>Instance</Th>
          {saysWhoCanJoin && <Th>Who can join</Th>}
          <Th className="text-right">Most at once</Th>
          <Th className="text-right">Seen for</Th>
        </>
      }
    >
      {view.others.map((other) => (
        <Tr key={other.instanceId}>
          <Td title={other.instanceId}>
            {other.modbotInstanceId ? (
              <button
                type="button"
                onClick={() => openInstance(other.modbotInstanceId!)}
                className="rounded-sm text-left font-medium hover:underline focus-visible:outline-2 focus-visible:outline-ring"
              >
                {otherLabel(other)}
              </button>
            ) : (
              <span className="font-mono">{otherLabel(other)}</span>
            )}
            {groupLabel(other, ownGroup) && (
              <span className="text-muted-foreground"> · {groupLabel(other, ownGroup)}</span>
            )}
            {isNamed(other) && (
              <div className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
                {other.number ? `#${other.number}` : other.instanceId}
              </div>
            )}
          </Td>
          {saysWhoCanJoin && (
            <Td className="text-muted-foreground">{accessInGame(other.groupAccessType) ?? (other.groupId ? 'Group' : '—')}</Td>
          )}
          <Td className="text-right font-mono">{other.peak}</Td>
          <Td className="text-right font-mono">
            {minutes((Date.parse(other.lastSeenAt) - Date.parse(other.firstSeenAt)) / 60000)}
          </Td>
        </Tr>
      ))}
    </Table>
  )
}

/** `#16354`, or the name it was opened with when Modbot knows the instance. */
function otherLabel(other: OtherInstance): string {
  return instanceNumber(other.number ?? other.instanceId, other.name)
}

/** The group's name after the label: the managed group's, or another group's once it is known. */
function groupLabel(other: OtherInstance, ownGroup: string): string | null {
  if (other.ownGroup) return ownGroup
  return other.groupName?.trim() || null
}

/** Whether the label is the instance's own name rather than its number. A blank name is no name. */
function isNamed(other: OtherInstance): boolean {
  return !!other.name?.trim()
}
