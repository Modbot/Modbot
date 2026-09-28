import { useCallback, useEffect, useRef } from 'react'
import { CartesianGrid, Line, LineChart, Tooltip, XAxis, YAxis, type TooltipContentProps } from 'recharts'
import { ChartFrame, ChartTooltip, chartHeight, compactNumber, dateTime, minutes, seriesColor } from '@/components/charts'
import { HeadCount } from '@/components/HeadCount'
import { InstanceWorld } from '@/components/subject/InstanceWorld'
import { SubjectLink, WorldLink } from '@/components/facts'
import { JsonView } from '@/components/JsonView'
import { Badge } from '@/components/ui/badge'
import { EmptyRow } from '@/components/PanelGrid'
import { Block, Empty, FactList, Field, Footer, More, Panel, PopupFrame, PopupMenu, PopupTabs } from '@/components/subject/shared'
import { INSTANCE_TABS, type InstanceTab as Tab } from '@/components/subject/tabs'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Stat, StatStrip } from '@/pages/analytics/shared'
import { readingTime, timeLabel, timeTicks } from '@/pages/analytics/memberCountSeries'
import { useLoad } from '@/lib/useLoad'
import { useOpenFromAbove } from '@/lib/useOpenFromAbove'
import { api, type CurrentUser, type InstanceView } from '@/lib/api'
import { concernsInstance } from '@/lib/liveRules'
import type { LiveEvent } from '@/lib/liveStream'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { accessInGame, headCountText, plural } from '@/lib/format'
import { instanceEnd, instanceName } from '@/lib/instanceName'
import { can } from '@/lib/permissions'
import { useOpeningTab } from '@/lib/subject'
import { vrchatMedia } from '@/lib/vrchatMedia'

/**
 * One instance: which world, which number, who can join and whether it is open on the left; where
 * and when it ran, how busy it got, who was in it and what happened there on the right.
 *
 * An instance's history is its log: the facts recorded there while it was open, which the Activity tab
 * already is. Opened by Modbot's own id for the instance, never VRChat's number, which VRChat hands
 * out again once an instance closes. The world is a link, so a moderator can go from "what happened
 * in here" to "what else runs in this world" without closing anything.
 */
export function InstancePopup({ id, me, lead }: { id: string; me: CurrentUser; lead?: React.ReactNode }) {
  const [tab, setTab] = useOpeningTab<Tab>('overview', INSTANCE_TABS)
  const [tabsAt, openFromAbove, pick] = useOpenFromAbove(setTab)
  const allowed = can(me, 'ViewAnalytics')

  // Read again when something happens in this instance. The stream names instances by VRChat's number,
  // which is only known once the instance has loaded, so the number is kept where the callback can
  // see it without being remade.
  const number = useRef<string | null>(null)
  const live = useLiveVersion(useCallback((event: LiveEvent) => concernsInstance(event, number.current), []))

  const load = useCallback(() => api.instance(id), [id])
  const { data, error } = useLoad(allowed ? load : null, live)

  useEffect(() => {
    number.current = data?.instance.vrChatInstanceId ?? null
  }, [data])

  if (!allowed) {
    return (
      <PopupFrame title="Instance" lead={lead} left={<Empty>You do not have permission to see instances.</Empty>}>
        <span />
      </PopupFrame>
    )
  }

  const instance = data?.instance
  const title = instance
    ? instanceName(instance.worldName, instance.worldId, instance.vrChatInstanceId, instance.instanceName)
    : 'Instance'

  return (
    <PopupFrame
      title={title}
      subtitle={instance ? <span title={instance.location}>{endLabel(instance)}</span> : undefined}
      lead={lead}
      actions={<PopupMenu onRawData={() => openFromAbove('json')} />}
      left={error ? <Empty tone="danger">{error}</Empty> : data ? <Identity view={data} /> : <Empty>Loading…</Empty>}
    >
      <PopupTabs
        at={tabsAt}
        value={tab}
        onChange={pick}
        tabs={[
          { value: 'overview', label: 'Overview' },
          { value: 'people', label: 'People', badge: data?.people.length },
          { value: 'logs', label: 'Activity', badge: data?.log.length },
          // Opened from the ⋯ in the header; a tab only while it is open, like the person popup's.
          ...(tab === 'json' ? [{ value: 'json' as const, label: 'Raw data' }] : []),
        ]}
      >
        {data && tab === 'overview' && <Overview view={data} live={live} onMore={pick} />}
        {data && !data.canSeeWhoWasThere && (tab === 'people' || tab === 'logs') && (
          <Panel title={tab === 'people' ? 'People' : 'Activity'} flush>
            <EmptyRow>You do not have permission to see this.</EmptyRow>
          </Panel>
        )}
        {data?.canSeeWhoWasThere && tab === 'people' && <People view={data} />}
        {data?.canSeeWhoWasThere && tab === 'logs' && (
          <Panel title="What happened in this instance" flush>
            <FactList entries={data.log} empty="Nothing recorded yet." now={data.now} />
            {data.logTruncated && (
              <Footer>Showing the newest {data.log.length}.</Footer>
            )}
          </Panel>
        )}
        {tab === 'json' && <JsonView title="Instance" value={error ?? data} className="border-0" />}
      </PopupTabs>
    </PopupFrame>
  )
}

function Identity({ view }: { view: InstanceView }) {
  const instance = view.instance
  const picture = view.worldImageUrl ?? instance.worldThumbnailImageUrl

  return (
    <>
      {picture && (
        <img
          src={vrchatMedia(picture)}
          alt=""
          className="aspect-[4/3] w-full shrink-0 border-b border-b-(length:--hairline) object-cover"
          loading="lazy"
        />
      )}

      <Block>
        <Field label="World" title={instance.worldId}>
          <WorldLink id={instance.worldId} name={instance.worldName} />
          <div className="font-mono text-muted-foreground break-all" style={{ fontSize: 'var(--text-tiny)' }}>
            {instance.worldId}
          </div>
        </Field>

        <Field label="Instance number" title={instance.location}>
          <span className="font-mono">{instance.vrChatInstanceId ?? '—'}</span>
        </Field>

        <Field label="Who can join">{accessInGame(instance.groupAccessType) ?? view.type ?? '—'}</Field>

        <Field label={endLabel(instance)}>
          {instance.closedAt ? (
            <>
              <span className="font-mono">{dateTime(instance.closedAt)}</span>
              {instance.closedBy === 'time' ? ' · went quiet' : ''}
            </>
          ) : (
            <>
              last seen <span className="font-mono">{dateTime(view.lastSeenAt)}</span>
            </>
          )}
        </Field>
      </Block>
    </>
  )
}

/** Where and when it ran, at the top of the Overview. How long and how busy are the figures under it. */
function Details({ view }: { view: InstanceView }) {
  const instance = view.instance

  return (
    <Panel title="Details">
      <div className="flex flex-wrap gap-x-6 gap-y-2">
        {instance.region && <Field label="Region">{instance.region.toUpperCase()}</Field>}
        <Field label="Opened">
          <span className="font-mono">{dateTime(instance.openedAt)}</span>
        </Field>
        {!instance.closedAt && (
          <Field label="People now">
            <span className="font-mono">
              <HeadCount count={instance.peopleNow ?? 0} unsure={instance.peopleNow !== null && instance.peopleNowUnsure} />
            </span>
          </Field>
        )}

        {view.canSeeWhoWasThere && (
          <Field label="Seen by a moderator's client">
            {view.counts.visitors > 0 ? (
              <>
                <span className="font-mono">{compactNumber(view.counts.visitors)}</span> people,{' '}
                <span className="font-mono">{minutes(view.counts.minutesSeen)}</span> of people-time
              </>
            ) : (
              'nobody'
            )}
          </Field>
        )}
      </div>
    </Panel>
  )
}

/** The glance: where and when, the figures, the people seen longest, the newest facts. */
function Overview({ view, live, onMore }: { view: InstanceView; live: number; onMore: (tab: Tab) => void }) {
  const instance = view.instance
  const longest = [...view.people].sort((a, b) => b.minutesSeen - a.minutesSeen).slice(0, 6)

  return (
    <div className="flex flex-col">
      <Details view={view} />

      <StatStrip className="m-0 shrink-0">
        <Stat label={instance.closedAt ? 'Ran for' : 'Open for'} value={minutes(instance.minutesOpen)} />
        <Stat
          label="Most at once"
          value={instance.peakPeople === null ? '—' : <HeadCount count={instance.peakPeople} unsure={instance.peakPeopleUnsure} />}
        />
        <Stat label="People seen" value={compactNumber(view.counts.visitors)} />
        <Stat label="Arrivals" value={compactNumber(view.counts.arrivals)} />
      </StatStrip>

      <PeopleOverTime view={view} />

      <InstanceWorld id={view.instance.id} live={live} />

      {view.canSeeWhoWasThere ? (
        <>
          <Panel
            title="Seen longest"
            right={<More onClick={() => onMore('people')}>Everybody</More>}
            flush={longest.length === 0}
          >
            {longest.length === 0 ? (
              <EmptyRow>Nobody seen.</EmptyRow>
            ) : (
              <ul className="flex flex-wrap gap-1.5" style={{ fontSize: 'var(--text-small)' }}>
                {longest.map((p) => (
                  <li key={p.userId}>
                    <Badge variant="outline" className="gap-1.5 text-foreground">
                      <SubjectLink id={p.userId} name={p.displayName} />
                      <span className="font-mono text-muted-foreground">{minutes(p.minutesSeen)}</span>
                    </Badge>
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          <Panel title="Latest" right={<More onClick={() => onMore('logs')}>All activity</More>} flush>
            <FactList entries={view.log.slice(0, 8)} empty="Nothing recorded yet." now={view.now} />
          </Panel>
        </>
      ) : (
        <Empty>You do not have permission to see who was here.</Empty>
      )}
    </div>
  )
}

/**
 * How many were in the instance, from when it opened until now or until it closed.
 *
 * A staircase, not a curve: a head count is kept only when it changes, so between two readings the
 * earlier one held. The last reading is carried to the end of the chart, because it held until then.
 * Not about who was there, so it shows for everyone who may open the instance.
 */
function PeopleOverTime({ view }: { view: InstanceView }) {
  const from = Date.parse(view.instance.openedAt)
  const to = Date.parse(view.instance.closedAt ?? view.now)
  const span = to - from

  const rows = view.headCounts.map((p) => ({ at: Date.parse(p.at), people: p.people, unsure: p.unsure }))
  if (rows.length > 0 && rows[rows.length - 1].at < to) rows.push({ ...rows[rows.length - 1], at: to })

  return (
    <Panel title="People over time">
      <ChartFrame height={chartHeight.regular} empty={rows.length === 0} emptyText="No head counts yet.">
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
    </Panel>
  )
}

/** One reading under the pointer: when, and how many -- "80?" when the count is unsure. */
function ReadingTooltip({ active, label, payload }: TooltipContentProps) {
  const reading = payload?.[0]?.payload as { people: number; unsure: boolean } | undefined
  if (!active || !reading) return null

  return (
    <ChartTooltip
      title={readingTime(Number(label))}
      rows={[
        {
          name: plural(reading.people, 'person', 'people'),
          value: headCountText(reading.people, reading.unsure),
          color: payload?.[0]?.color,
        },
      ]}
    />
  )
}

function People({ view }: { view: InstanceView }) {
  return (
    <Panel title="Who was seen in this instance" flush>
      {view.people.length === 0 ? (
        <EmptyRow>Nobody seen.</EmptyRow>
      ) : (
        <Table
          head={
            <>
              <Th>Person</Th>
              <Th className="text-right">Time seen</Th>
              <Th className="text-right">Arrivals</Th>
              <Th>First seen</Th>
              <Th>Last seen</Th>
            </>
          }
        >
          {view.people.map((p) => (
            <Tr key={p.userId}>
              <Td>
                <SubjectLink id={p.userId} name={p.displayName} />
                {p.displayName && (
                  <div className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
                    {p.userId}
                  </div>
                )}
              </Td>
              <Td className="text-right font-mono">{minutes(p.minutesSeen)}</Td>
              <Td className="text-right font-mono">{p.arrivals}</Td>
              <Td className="font-mono text-muted-foreground">{dateTime(p.firstSeenAt)}</Td>
              <Td className="font-mono text-muted-foreground">{dateTime(p.lastSeenAt)}</Td>
            </Tr>
          ))}
        </Table>
      )}
    </Panel>
  )
}

/** "Open now", "Closed" (by a moderator) or "Ended" (on its own), as a heading. */
function endLabel(instance: Parameters<typeof instanceEnd>[0]): string {
  const end = instanceEnd(instance)
  return end.charAt(0).toUpperCase() + end.slice(1)
}
