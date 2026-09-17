import { useCallback } from 'react'
import { Tabs } from '@/components/ui/tabs'
import { compactNumber, dateTime, minutes } from '@/components/charts'
import { SubjectLink, WorldLink } from '@/components/facts'
import { JsonView } from '@/components/JsonView'
import { FactList, Field, Figure, Note, Panel, PopupFrame } from '@/components/subject/shared'
import { useLoad } from '@/lib/useLoad'
import { api, type CurrentUser, type InstanceView } from '@/lib/api'
import { access } from '@/lib/format'
import { can } from '@/lib/permissions'
import { useOpeningTab } from '@/lib/subject'

const TABS = ['overview', 'people', 'logs', 'json'] as const
type Tab = (typeof TABS)[number]

/**
 * One room: where and when it ran and how busy it got on the left; who was in it and what
 * happened there on the right.
 *
 * A room's history is its log: the facts recorded there while it was open, which the Logs tab
 * already is. Opened by Modbot's own id for the room, never VRChat's number, which VRChat hands
 * out again once a room closes. The world is a link, so a moderator can go from "what happened
 * in here" to "what else runs in this world" without closing anything.
 */
export function InstancePopup({ id, me, lead }: { id: string; me: CurrentUser; lead?: React.ReactNode }) {
  const [tab, setTab] = useOpeningTab<Tab>('overview', TABS)
  const allowed = can(me, 'ViewAnalytics')

  const load = useCallback(() => api.instance(id), [id])
  const { data, error } = useLoad(allowed ? load : null)

  if (!allowed) {
    return (
      <PopupFrame title="Instance" lead={lead} left={<Note>You do not have permission to see instances.</Note>}>
        <span />
      </PopupFrame>
    )
  }

  const room = data?.room
  const title = room
    ? `${room.worldName ?? 'Unnamed world'}${room.vrChatInstanceId ? ` · ${room.vrChatInstanceId}` : ''}`
    : 'Instance'

  return (
    <PopupFrame
      title={title}
      subtitle={room ? <span title={room.location}>{room.closedAt ? 'Closed' : 'Open now'}</span> : undefined}
      lead={lead}
      left={error ? <Note className="text-destructive">{error}</Note> : data ? <Identity view={data} /> : <Note>Loading…</Note>}
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
          <Panel title={tab === 'people' ? 'People' : 'Logs'}>
            <Note>You do not have permission to see this.</Note>
          </Panel>
        )}
        {data?.canSeeWhoWasThere && tab === 'people' && <People view={data} />}
        {data?.canSeeWhoWasThere && tab === 'logs' && (
          <Panel title="What happened in this instance">
            <FactList entries={data.log} empty="Nothing recorded yet." />
            {data.logTruncated && <Note>Showing the newest {data.log.length}.</Note>}
          </Panel>
        )}
        {tab === 'json' && (
          <div className="p-4">
            <JsonView title="Instance" value={error ?? data} />
          </div>
        )}
      </Tabs>
    </PopupFrame>
  )
}

function Identity({ view }: { view: InstanceView }) {
  const room = view.room
  const picture = view.worldImageUrl ?? room.worldThumbnailImageUrl
  const openFor = minutes(room.minutesOpen)

  return (
    <>
      {picture && <img src={picture} alt="" className="aspect-[4/3] w-full rounded-xl object-cover" loading="lazy" />}

      <Field label="World" title={room.worldId}>
        <WorldLink id={room.worldId} name={room.worldName} />
        <div className="font-mono text-muted-foreground break-all" style={{ fontSize: 'var(--text-tiny, 11px)' }}>
          {room.worldId}
        </div>
      </Field>

      <Field label="Instance number" title={room.location}>
        <span className="font-mono">{room.vrChatInstanceId ?? '—'}</span>
      </Field>

      <Field label="Who can join">{access(room.groupAccessType) ?? view.type ?? '—'}</Field>
      {room.region && <Field label="Region">{room.region.toUpperCase()}</Field>}

      <Field label="Opened">{dateTime(room.openedAt)}</Field>
      <Field label={room.closedAt ? 'Closed' : 'Still open'}>
        {room.closedAt
          ? `${dateTime(room.closedAt)}${room.closedBy === 'time' ? ' · went quiet' : ''}`
          : `last seen ${dateTime(view.lastSeenAt)}`}
      </Field>
      <Field label={room.closedAt ? 'Ran for' : 'Open for'}>{openFor}</Field>

      {!room.closedAt && <Field label="People now">{room.peopleNow ?? 0}</Field>}
      <Field label="Most at once">{room.peakPeople ?? '—'}</Field>

      {view.canSeeWhoWasThere && (
        <Field label="Seen by a moderator's client">
          {view.counts.visitors > 0
            ? `${compactNumber(view.counts.visitors)} people, ${minutes(view.counts.minutesSeen)} of people-time`
            : 'nobody'}
        </Field>
      )}
    </>
  )
}

/** The glance: the figures, the people seen longest, the newest facts. */
function Overview({ view, onMore }: { view: InstanceView; onMore: (tab: Tab) => void }) {
  const room = view.room
  const longest = [...view.people].sort((a, b) => b.minutesSeen - a.minutesSeen).slice(0, 6)

  return (
    <div className="flex flex-col gap-3 p-4">
      <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
        <Figure label={room.closedAt ? 'Ran for' : 'Open for'} value={minutes(room.minutesOpen)} />
        <Figure label="Most at once" value={room.peakPeople === null ? '—' : String(room.peakPeople)} />
        <Figure label="People seen" value={compactNumber(view.counts.visitors)} />
        <Figure label="Arrivals" value={compactNumber(view.counts.arrivals)} />
      </div>

      {view.canSeeWhoWasThere ? (
        <>
          <div className="flex items-center gap-2">
            <span className="font-medium">Seen longest</span>
            <span className="flex-1" />
            <button type="button" onClick={() => onMore('people')} className="text-muted-foreground hover:text-foreground hover:underline" style={{ fontSize: 'var(--text-small)' }}>
              Everybody
            </button>
          </div>
          {longest.length === 0 ? (
            <Note>Nobody seen.</Note>
          ) : (
            <ul className="flex flex-wrap gap-2" style={{ fontSize: 'var(--text-small)' }}>
              {longest.map((p) => (
                <li key={p.userId} className="rounded-md border px-2 py-1" style={{ borderWidth: 'var(--hairline)' }}>
                  <SubjectLink id={p.userId} name={p.displayName} />{' '}
                  <span className="tabular-nums text-muted-foreground">{minutes(p.minutesSeen)}</span>
                </li>
              ))}
            </ul>
          )}

          <div className="flex items-center gap-2">
            <span className="font-medium">Latest</span>
            <span className="flex-1" />
            <button type="button" onClick={() => onMore('logs')} className="text-muted-foreground hover:text-foreground hover:underline" style={{ fontSize: 'var(--text-small)' }}>
              All logs
            </button>
          </div>
          <FactList entries={view.log.slice(0, 8)} empty="Nothing recorded yet." />
        </>
      ) : (
        <Note>You do not have permission to see who was here.</Note>
      )}
    </div>
  )
}

function People({ view }: { view: InstanceView }) {
  return (
    <Panel title="Who was seen in this instance">
      {view.people.length === 0 ? (
        <Note>Nobody seen.</Note>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
            <thead className="text-left text-muted-foreground">
              <tr>
                <th className="py-1 pr-3 font-medium">Person</th>
                <th className="py-1 pr-3 text-right font-medium">Time seen</th>
                <th className="py-1 pr-3 text-right font-medium">Arrivals</th>
                <th className="py-1 pr-3 font-medium">First seen</th>
                <th className="py-1 font-medium">Last seen</th>
              </tr>
            </thead>
            <tbody>
              {view.people.map((p) => (
                <tr key={p.userId} className="border-t" style={{ borderTopWidth: 'var(--hairline)' }}>
                  <td className="py-1 pr-3">
                    <SubjectLink id={p.userId} name={p.displayName} />
                    {p.displayName && (
                      <div className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-tiny, 11px)' }}>
                        {p.userId}
                      </div>
                    )}
                  </td>
                  <td className="py-1 pr-3 text-right tabular-nums">{minutes(p.minutesSeen)}</td>
                  <td className="py-1 pr-3 text-right tabular-nums">{p.arrivals}</td>
                  <td className="py-1 pr-3 text-muted-foreground">{dateTime(p.firstSeenAt)}</td>
                  <td className="py-1 text-muted-foreground">{dateTime(p.lastSeenAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}
