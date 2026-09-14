import { useCallback, useState } from 'react'
import { Tabs } from '@/components/ui/tabs'
import { DailyBars, compactNumber, dateTime, minutes } from '@/components/charts'
import { SubjectLink } from '@/components/facts'
import { RoomTable } from '@/components/RoomTable'
import { Field, Figure, Note, Panel, PopupFrame } from '@/components/subject/shared'
import { useLoad } from '@/lib/useLoad'
import { api, type CurrentUser, type WorldView } from '@/lib/api'
import { ago, formatDay } from '@/lib/format'
import { can } from '@/lib/permissions'

type Tab = 'instances' | 'metrics'

/**
 * One world: its page as Modbot last read it on the left, the rooms that have run in it and how
 * busy it has been on the right.
 *
 * Everything shown is from Modbot's own tables. Opening this never asks VRChat for anything.
 */
export function WorldPopup({ id, me, lead }: { id: string; me: CurrentUser; lead?: React.ReactNode }) {
  const [tab, setTab] = useState<Tab>('instances')
  const allowed = can(me, 'ViewAnalytics')

  const load = useCallback(() => api.world(id), [id])
  const { data, error } = useLoad(allowed ? load : null)

  const title = data?.name ?? 'World'

  if (!allowed) {
    return (
      <PopupFrame title="World" subtitle={<Id id={id} />} lead={lead} left={<Id id={id} />}>
        <Panel title="World">
          <Note>Worlds are part of Analytics, which your account cannot open.</Note>
        </Panel>
      </PopupFrame>
    )
  }

  return (
    <PopupFrame
      title={title}
      subtitle={<Id id={id} />}
      lead={lead}
      left={error ? <Note className="text-destructive">{error}</Note> : data ? <Identity world={data} /> : <Note>Loading…</Note>}
    >
      <Tabs
        value={tab}
        onChange={setTab}
        tabs={[
          { value: 'instances', label: 'Instances', badge: data?.roomsTotal },
          { value: 'metrics', label: 'Metrics' },
        ]}
      >
        {data && tab === 'instances' && (
          <Panel
            title="Instances in this world"
            note="Newest first, from the group's own instance list. A room open in a world the group does not run appears only if a moderator's client was in it."
          >
            {data.rooms.length === 0 ? (
              <Note>No instance in this world has been recorded.</Note>
            ) : (
              <>
                <RoomTable rooms={data.rooms} showWorld={false} />
                {data.roomsTotal > data.rooms.length && (
                  <Note>
                    Showing the newest {data.rooms.length} of {compactNumber(data.roomsTotal)}.
                  </Note>
                )}
              </>
            )}
          </Panel>
        )}
        {data && tab === 'metrics' && <Metrics world={data} />}
      </Tabs>
    </PopupFrame>
  )
}

function Id({ id }: { id: string }) {
  return (
    <span className="font-mono break-all" title={id}>
      {id}
    </span>
  )
}

function Identity({ world }: { world: WorldView }) {
  const picture = world.imageUrl ?? world.thumbnailImageUrl

  return (
    <>
      {picture && (
        <img src={picture} alt="" className="aspect-[4/3] w-full rounded-md object-cover" loading="lazy" />
      )}

      <div>
        <div className="text-lg font-semibold tracking-tight">
          {world.name ?? <span className="text-muted-foreground">Name not read yet</span>}
        </div>
        {!world.name && (
          // Ordinary, not an error: the world sweep reads a page shortly after the id is first
          // seen, and a private or deleted world never gets a name at all.
          <Note>
            {world.readError
              ? `Modbot tried to read this world's page and VRChat said: ${world.readError}`
              : world.known
                ? 'Modbot has seen this world but has not read its page yet. The name appears once it has.'
                : 'Modbot has only ever seen this id. Nothing else about the world is known.'}
          </Note>
        )}
      </div>

      {world.authorName && (
        <Field label="Made by">
          {world.authorId ? <SubjectLink id={world.authorId} name={world.authorName} /> : world.authorName}
        </Field>
      )}

      {/* What the page said. Never a limit Modbot enforces -- exemptions raise real capacity
          above it (spec 3.1). */}
      {world.capacity !== null && (
        <Field label="Holds">
          {world.capacity} people
          {world.recommendedCapacity !== null && world.recommendedCapacity !== world.capacity
            ? `, ${world.recommendedCapacity} suggested`
            : ''}
        </Field>
      )}

      {world.releaseStatus && <Field label="Who can find it">{releaseWords(world.releaseStatus)}</Field>}
      {world.firstSeenAt && <Field label="First seen by Modbot">{formatDay(world.firstSeenAt)}</Field>}
      {world.publishedAt && <Field label="Published on VRChat">{formatDay(world.publishedAt)}</Field>}

      <Field label="Page last read">
        {world.lastReadAt ? `${ago(world.lastReadAt, world.now)} (${dateTime(world.lastReadAt)})` : 'never'}
      </Field>

      {world.description && (
        <Field label="Description">
          <span className="whitespace-pre-wrap">{world.description}</span>
        </Field>
      )}
    </>
  )
}

/** VRChat's release status, in a word a member would use. An unknown word is shown as sent. */
function releaseWords(status: string): string {
  return { public: 'Anyone (public)', private: 'Only people given the link (private)', hidden: 'Hidden' }[status] ?? status
}

function Metrics({ world }: { world: WorldView }) {
  const c = world.counts
  const series = [...world.visitorsPerDay, ...world.roomsPerDay].map((p) => p.day).sort()
  const from = series[0]
  const to = series[series.length - 1]

  return (
    <Panel
      title="How busy this world has been"
      note="Time and visitors come from the desktop client's presence reports, so they only count rooms a moderator's client was in. Instances opened come from the group's own list and are complete."
    >
      <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
        <Figure label="Time seen" value={minutes(c.minutesSeen)} note="people-time, while a client was watching" />
        <Figure label="Visitors" value={compactNumber(c.visitors)} note="distinct people" />
        <Figure label="Instances opened" value={compactNumber(world.roomsTotal)} note={`${world.roomsOpenNow} open now`} />
        <Figure label="Last seen" value={c.lastSeenAt ? ago(c.lastSeenAt, world.now) : '—'} />
      </div>

      {from && to ? (
        <>
          <div className="mt-2 font-medium">Visitors per day</div>
          <DailyBars
            from={from}
            to={to}
            series={[{ key: 'visitors', label: 'visitors', points: world.visitorsPerDay, slot: 1 }]}
            emptyText="No visitors recorded in the daily totals yet."
          />
          <div className="mt-2 font-medium">Instances opened per day</div>
          <DailyBars
            from={from}
            to={to}
            series={[{ key: 'rooms', label: 'instances opened', points: world.roomsPerDay, slot: 4 }]}
            emptyText="No instances opened in the daily totals yet."
          />
          <Note>From the daily totals, which are about fifteen minutes behind.</Note>
        </>
      ) : (
        <Note>Nothing about this world has reached the daily totals yet.</Note>
      )}
    </Panel>
  )
}
