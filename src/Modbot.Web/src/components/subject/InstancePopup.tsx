import { useCallback, useEffect, useRef } from 'react'
import { Tabs } from '@/components/ui/tabs'
import { compactNumber, dateTime, minutes } from '@/components/charts'
import { SubjectLink, WorldLink } from '@/components/facts'
import { JsonView } from '@/components/JsonView'
import { Badge } from '@/components/ui/badge'
import { EmptyRow } from '@/components/PanelGrid'
import { Block, Empty, FactList, Field, Footer, More, Panel, PopupFrame } from '@/components/subject/shared'
import { Stat, StatStrip, Table, Td, Th, Tr } from '@/pages/analytics/shared'
import { useLoad } from '@/lib/useLoad'
import { api, type CurrentUser, type InstanceView } from '@/lib/api'
import { concernsInstance } from '@/lib/liveRules'
import type { LiveEvent } from '@/lib/liveStream'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { access } from '@/lib/format'
import { instanceName } from '@/lib/instanceName'
import { can } from '@/lib/permissions'
import { useOpeningTab } from '@/lib/subject'
import { vrchatMedia } from '@/lib/vrchatMedia'

const TABS = ['overview', 'people', 'logs', 'json'] as const
type Tab = (typeof TABS)[number]

/**
 * One instance: which world, which number, who can join and whether it is open on the left; where
 * and when it ran, how busy it got, who was in it and what happened there on the right.
 *
 * An instance's history is its log: the facts recorded there while it was open, which the Logs tab
 * already is. Opened by Modbot's own id for the instance, never VRChat's number, which VRChat hands
 * out again once an instance closes. The world is a link, so a moderator can go from "what happened
 * in here" to "what else runs in this world" without closing anything.
 */
export function InstancePopup({ id, me, lead }: { id: string; me: CurrentUser; lead?: React.ReactNode }) {
  const [tab, setTab] = useOpeningTab<Tab>('overview', TABS)
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
  const title = instance ? instanceName(instance.worldName, instance.worldId, instance.vrChatInstanceId) : 'Instance'

  return (
    <PopupFrame
      title={title}
      subtitle={instance ? <span title={instance.location}>{instance.closedAt ? 'Closed' : 'Open now'}</span> : undefined}
      lead={lead}
      left={error ? <Empty className="text-destructive">{error}</Empty> : data ? <Identity view={data} /> : <Empty>Loading…</Empty>}
    >
      <Tabs
        value={tab}
        onChange={setTab}
        tabs={[
          { value: 'overview', label: 'Overview' },
          { value: 'people', label: 'People', badge: data?.people.length },
          { value: 'logs', label: 'Logs', badge: data?.log.length },
          { value: 'json', label: 'JSON' },
        ]}
      >
        {data && tab === 'overview' && <Overview view={data} onMore={setTab} />}
        {data && !data.canSeeWhoWasThere && (tab === 'people' || tab === 'logs') && (
          <Panel title={tab === 'people' ? 'People' : 'Logs'} flush>
            <EmptyRow>You do not have permission to see this.</EmptyRow>
          </Panel>
        )}
        {data?.canSeeWhoWasThere && tab === 'people' && <People view={data} />}
        {data?.canSeeWhoWasThere && tab === 'logs' && (
          <Panel title="What happened in this instance" flush>
            <FactList entries={data.log} empty="Nothing recorded yet." />
            {data.logTruncated && (
              <Footer>Showing the newest {data.log.length}.</Footer>
            )}
          </Panel>
        )}
        {tab === 'json' && <JsonView title="Instance" value={error ?? data} className="border-0" />}
      </Tabs>
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
          <div className="font-mono text-muted-foreground break-all" style={{ fontSize: 'var(--text-tiny, 11px)' }}>
            {instance.worldId}
          </div>
        </Field>

        <Field label="Instance number" title={instance.location}>
          <span className="font-mono">{instance.vrChatInstanceId ?? '—'}</span>
        </Field>

        <Field label="Who can join">{access(instance.groupAccessType) ?? view.type ?? '—'}</Field>

        <Field label={instance.closedAt ? 'Closed' : 'Open now'}>
          {instance.closedAt
            ? `${dateTime(instance.closedAt)}${instance.closedBy === 'time' ? ' · went quiet' : ''}`
            : `last seen ${dateTime(view.lastSeenAt)}`}
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
            <span className="font-mono">{instance.peopleNow ?? 0}</span>
          </Field>
        )}

        {view.canSeeWhoWasThere && (
          <Field label="Seen by a moderator's client">
            {view.counts.visitors > 0
              ? `${compactNumber(view.counts.visitors)} people, ${minutes(view.counts.minutesSeen)} of people-time`
              : 'nobody'}
          </Field>
        )}
      </div>
    </Panel>
  )
}

/** The glance: where and when, the figures, the people seen longest, the newest facts. */
function Overview({ view, onMore }: { view: InstanceView; onMore: (tab: Tab) => void }) {
  const instance = view.instance
  const longest = [...view.people].sort((a, b) => b.minutesSeen - a.minutesSeen).slice(0, 6)

  return (
    <div className="flex flex-col">
      <Details view={view} />

      <StatStrip className="m-0 shrink-0">
        <Stat label={instance.closedAt ? 'Ran for' : 'Open for'} value={minutes(instance.minutesOpen)} />
        <Stat label="Most at once" value={instance.peakPeople === null ? '—' : String(instance.peakPeople)} />
        <Stat label="People seen" value={compactNumber(view.counts.visitors)} />
        <Stat label="Arrivals" value={compactNumber(view.counts.arrivals)} />
      </StatStrip>

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

          <Panel title="Latest" right={<More onClick={() => onMore('logs')}>All logs</More>} flush>
            <FactList entries={view.log.slice(0, 8)} empty="Nothing recorded yet." />
          </Panel>
        </>
      ) : (
        <Empty>You do not have permission to see who was here.</Empty>
      )}
    </div>
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
                  <div className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-tiny, 11px)' }}>
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
