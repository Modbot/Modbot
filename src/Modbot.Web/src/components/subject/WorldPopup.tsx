import { useCallback } from 'react'
import { Tabs } from '@/components/ui/tabs'
import { DailyBars, compactNumber, dateTime, minutes } from '@/components/charts'
import { SubjectLink } from '@/components/facts'
import { JsonView } from '@/components/JsonView'
import { InstanceTable } from '@/components/InstanceTable'
import { FactList, Field, Figure, Note, Panel, PopupFrame } from '@/components/subject/shared'
import { useLoad } from '@/lib/useLoad'
import { api, type CurrentUser, type WorldView } from '@/lib/api'
import { concernsWorld } from '@/lib/liveRules'
import type { LiveEvent } from '@/lib/liveStream'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { ago, formatDay } from '@/lib/format'
import { can } from '@/lib/permissions'
import { useOpeningTab } from '@/lib/subject'

const TABS = ['overview', 'instances', 'history', 'metrics', 'json'] as const
type Tab = (typeof TABS)[number]

/**
 * One world: its name, picture, author and who can find it on the left; the rest of its page, the
 * instances that have run in it and how busy it has been on the right.
 *
 * A world has no versions of its own -- the page is read once and left alone -- so History is
 * what happened in it: every fact recorded in one of its instances, newest first. Everything shown is
 * from Modbot's own tables. Opening this never asks VRChat for anything.
 */
export function WorldPopup({ id, me, lead }: { id: string; me: CurrentUser; lead?: React.ReactNode }) {
  const [tab, setTab] = useOpeningTab<Tab>('overview', TABS)
  const allowed = can(me, 'ViewAnalytics')

  // Read again when something happens in one of this world's instances.
  const live = useLiveVersion(useCallback((event: LiveEvent) => concernsWorld(event, id), [id]))

  const load = useCallback(() => api.world(id), [id])
  const { data, error } = useLoad(allowed ? load : null, live)

  const title = data?.name ?? 'World'

  if (!allowed) {
    return (
      <PopupFrame title="World" subtitle={<Id id={id} />} lead={lead} left={<Id id={id} />}>
        <Panel title="World">
          <Note>You do not have permission to see worlds.</Note>
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
          { value: 'overview', label: 'Overview' },
          { value: 'instances', label: 'Instances', badge: data?.instancesTotal },
          { value: 'history', label: 'History' },
          { value: 'metrics', label: 'Metrics' },
          { value: 'json', label: 'JSON' },
        ]}
      >
        {data && tab === 'overview' && <Overview world={data} onMore={setTab} />}
        {data && tab === 'instances' && (
          <Panel title="Instances in this world">
            {data.instances.length === 0 ? (
              <Note>No instances yet.</Note>
            ) : (
              <>
                <InstanceTable instances={data.instances} showWorld={false} />
                {data.instancesTotal > data.instances.length && (
                  <Note>
                    Showing the newest {data.instances.length} of {compactNumber(data.instancesTotal)}.
                  </Note>
                )}
              </>
            )}
          </Panel>
        )}
        {tab === 'history' && <History id={id} />}
        {data && tab === 'metrics' && <Metrics world={data} />}
        {tab === 'json' && (
          <div className="p-4">
            <JsonView title="World" value={error ?? data} />
          </div>
        )}
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
        <img src={picture} alt="" className="aspect-[4/3] w-full rounded-xl object-cover" loading="lazy" />
      )}

      <div>
        <div className="font-display text-lg">
          {world.name ?? <span className="text-muted-foreground">Name not read yet</span>}
        </div>
        {/* A missing name is ordinary, not an error: the world sweep reads a page shortly after
            the id is first seen, and a private or deleted world never gets a name at all. Only a
            failed read has anything to say. */}
        {!world.name && world.readError && (
          <Note className="text-destructive">Couldn't read this world: {world.readError}</Note>
        )}
      </div>

      {world.authorName && (
        <Field label="Made by">
          {world.authorId ? <SubjectLink id={world.authorId} name={world.authorName} /> : world.authorName}
        </Field>
      )}

      {world.releaseStatus && <Field label="Who can find it">{releaseWords(world.releaseStatus)}</Field>}
    </>
  )
}

/** The rest of the page as Modbot read it, at the top of the Overview. */
function Details({ world }: { world: WorldView }) {
  return (
    <div className="flex flex-col gap-2">
      <div className="font-medium">Details</div>

      <div className="flex flex-wrap gap-x-6 gap-y-2">
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

        {world.firstSeenAt && <Field label="First seen by Modbot">{formatDay(world.firstSeenAt)}</Field>}
        {world.publishedAt && <Field label="Published on VRChat">{formatDay(world.publishedAt)}</Field>}

        <Field label="Page last read">
          {world.lastReadAt ? `${ago(world.lastReadAt, world.now)} (${dateTime(world.lastReadAt)})` : 'never'}
        </Field>
      </div>

      {world.description && (
        <Field label="Description">
          {/* The author's text, rendered as text. Never as HTML. */}
          <span className="whitespace-pre-wrap">{world.description}</span>
        </Field>
      )}
    </div>
  )
}

/** VRChat's release status, in a word a member would use. An unknown word is shown as sent. */
function releaseWords(status: string): string {
  return { public: 'Anyone (public)', private: 'Only people given the link (private)', hidden: 'Hidden' }[status] ?? status
}

/** The glance: the rest of the page, the figures, the newest instances, and where to go for the rest. */
function Overview({ world, onMore }: { world: WorldView; onMore: (tab: Tab) => void }) {
  const c = world.counts

  return (
    <div className="flex flex-col gap-3 p-4">
      <Details world={world} />

      <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
        <Figure label="Time seen" value={minutes(c.minutesSeen)} />
        <Figure label="Visitors" value={compactNumber(c.visitors)} />
        <Figure label="Instances opened" value={compactNumber(world.instancesTotal)} note={`${world.instancesOpenNow} open now`} />
        <Figure label="Last seen" value={c.lastSeenAt ? ago(c.lastSeenAt, world.now) : '—'} />
      </div>

      <div className="flex items-center gap-2">
        <span className="font-medium">Newest instances</span>
        <span className="flex-1" />
        <button type="button" onClick={() => onMore('instances')} className="text-muted-foreground hover:text-foreground hover:underline" style={{ fontSize: 'var(--text-small)' }}>
          All instances
        </button>
      </div>

      {world.instances.length === 0 ? <Note>No instances yet.</Note> : <InstanceTable instances={world.instances.slice(0, 5)} showWorld={false} />}
    </div>
  )
}

/** Every fact recorded in one of this world's instances, newest first. */
function History({ id }: { id: string }) {
  const live = useLiveVersion(useCallback((event: LiveEvent) => concernsWorld(event, id), [id]))
  const load = useCallback(() => api.audit({ world: id, limit: 50 }), [id])
  const { data, error } = useLoad(load, live)

  return (
    <Panel title="What happened in this world">
      {error && <Note className="text-destructive">{error}</Note>}
      {!error && !data && <Note>Loading…</Note>}
      {data && <FactList entries={data.entries} empty="Nothing recorded yet." />}
    </Panel>
  )
}

function Metrics({ world }: { world: WorldView }) {
  const c = world.counts
  const series = [...world.visitorsPerDay, ...world.instancesPerDay].map((p) => p.day).sort()
  const from = series[0]
  const to = series[series.length - 1]

  return (
    <Panel title="How busy this world has been">
      <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
        <Figure label="Time seen" value={minutes(c.minutesSeen)} />
        <Figure label="Visitors" value={compactNumber(c.visitors)} />
        <Figure label="Instances opened" value={compactNumber(world.instancesTotal)} note={`${world.instancesOpenNow} open now`} />
        <Figure label="Last seen" value={c.lastSeenAt ? ago(c.lastSeenAt, world.now) : '—'} />
      </div>

      {from && to ? (
        <>
          <div className="mt-2 font-medium">Visitors per day</div>
          <DailyBars
            from={from}
            to={to}
            series={[{ key: 'visitors', label: 'visitors', points: world.visitorsPerDay, slot: 1 }]}
            emptyText="No visitors yet."
          />
          <div className="mt-2 font-medium">Instances opened per day</div>
          <DailyBars
            from={from}
            to={to}
            series={[{ key: 'instances', label: 'instances opened', points: world.instancesPerDay, slot: 4 }]}
            emptyText="No instances yet."
          />
        </>
      ) : (
        <Note>Nothing recorded yet.</Note>
      )}
    </Panel>
  )
}
