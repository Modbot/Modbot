import { useCallback } from 'react'
import { DailyBars, compactNumber, dateTime, minutes } from '@/components/charts'
import { SubjectLink } from '@/components/facts'
import { JsonView } from '@/components/JsonView'
import { InstanceTable } from '@/components/InstanceTable'
import { EmptyRow } from '@/components/PanelGrid'
import {
  Block,
  CopyId,
  Empty,
  FactList,
  Field,
  Footer,
  HeaderPicture,
  More,
  Note,
  Panel,
  PopupFrame,
  PopupMenu,
  PopupTabs,
} from '@/components/subject/shared'
import { WORLD_MOVED, WORLD_TABS, type WorldTab as Tab } from '@/components/subject/tabs'
import { Stat, StatStrip } from '@/pages/analytics/shared'
import { useLoad } from '@/lib/useLoad'
import { usePhoneLayout } from '@/lib/phoneLayout'
import { useOpenFromAbove } from '@/lib/useOpenFromAbove'
import { api, type CurrentUser, type WorldView } from '@/lib/api'
import { concernsWorld } from '@/lib/liveRules'
import type { LiveEvent } from '@/lib/liveStream'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { ago, formatDay } from '@/lib/format'
import { Ago } from '@/components/Freshness'
import { can } from '@/lib/permissions'
import { useOpeningTab } from '@/lib/subject'
import { vrchatMedia } from '@/lib/vrchatMedia'

/**
 * One world: its name, picture, author and who can find it on the left; the rest of its page, the
 * instances that have run in it and how busy it has been on the right.
 *
 * A world has no versions of its own -- the page is read once and left alone -- so History is
 * what happened in it: every fact recorded in one of its instances, newest first. Everything shown is
 * from Modbot's own tables. Opening this never asks VRChat for anything.
 */
export function WorldPopup({ id, me, lead }: { id: string; me: CurrentUser; lead?: React.ReactNode }) {
  const [tab, setTab] = useOpeningTab<Tab>('overview', WORLD_TABS, WORLD_MOVED)
  const [tabsAt, openFromAbove, pick] = useOpenFromAbove(setTab)
  const allowed = can(me, 'ViewAnalytics')

  // Read again when something happens in one of this world's instances.
  const live = useLiveVersion(useCallback((event: LiveEvent) => concernsWorld(event, id), [id]))

  const load = useCallback(() => api.world(id), [id])
  const { data, error, reload } = useLoad(allowed ? load : null, live)

  // On a phone the picture is small, in the header, and the facts from the left column are at the
  // top of Overview instead: a column stacked above the tabs was mostly picture and put the tab
  // row two screens down on a phone on its side (mobile review 2026-09-28, rule 5). Drawn in one
  // place or the other, never both.
  const phone = usePhoneLayout()

  const title = data?.name ?? 'World'

  if (!allowed) {
    return (
      <PopupFrame
        title="World"
        subtitle={<CopyId id={id} />}
        lead={lead}
        left={
          <Block>
            <Id id={id} />
          </Block>
        }
      >
        <Panel title="World" flush>
          <EmptyRow>You do not have permission to see worlds.</EmptyRow>
        </Panel>
      </PopupFrame>
    )
  }

  return (
    <PopupFrame
      title={title}
      subtitle={<CopyId id={id} />}
      lead={
        <>
          {lead}
          {phone && !lead && !error && <HeaderPicture url={data ? (data.thumbnailImageUrl ?? data.imageUrl) : undefined} />}
        </>
      }
      actions={<PopupMenu onRawData={() => openFromAbove('json')} />}
      left={
        error ? (
          <Empty tone="danger" onTryAgain={reload}>{error}</Empty>
        ) : !data ? (
          <Empty tone="loading" />
        ) : phone ? null : (
          <Identity world={data} />
        )
      }
    >
      <PopupTabs
        at={tabsAt}
        value={tab}
        onChange={pick}
        tabs={[
          { value: 'overview', label: 'Overview' },
          { value: 'instances', label: 'Instances', badge: data?.instancesTotal },
          { value: 'history', label: 'History' },
          // Opened from the ⋯ in the header; a tab only while it is open, like the person popup's.
          ...(tab === 'json' ? [{ value: 'json' as const, label: 'Raw data' }] : []),
        ]}
      >
        {data && tab === 'overview' && <Overview world={data} phone={phone} onMore={pick} />}
        {data && tab === 'instances' && (
          <Panel title="Instances in this world" flush>
            {data.instances.length === 0 ? (
              <EmptyRow>No instances yet.</EmptyRow>
            ) : (
              <>
                <InstanceTable instances={data.instances} showWorld={false} />
                {data.instancesTotal > data.instances.length && (
                  <Footer>
                    Showing the newest {data.instances.length} of {compactNumber(data.instancesTotal)}.
                  </Footer>
                )}
              </>
            )}
          </Panel>
        )}
        {tab === 'history' && <History id={id} />}
        {tab === 'json' && <JsonView title="World" value={error ?? data} className="border-0" />}
      </PopupTabs>
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
        <img
          src={vrchatMedia(picture)}
          alt=""
          className="aspect-[4/3] w-full shrink-0 border-b border-b-(length:--hairline) object-cover"
          loading="lazy"
        />
      )}

      <Block>
        <div>
          <div className="font-medium break-words" style={{ fontSize: 'var(--text-base)' }}>
            {world.name ?? <span className="text-muted-foreground">Name not read yet</span>}
          </div>
          {/* A missing name is ordinary, not an error: the world sweep reads a page shortly after
              the id is first seen, and a private or deleted world never gets a name at all. Only a
              failed read has anything to say. */}
          {!world.name && world.readError && (
            <Note className="text-destructive">Couldn't read this world: {world.readError}</Note>
          )}
        </div>

        <Maker world={world} />
      </Block>
    </>
  )
}

/** Who made it and who can find it: under the name on a desk, first in Details on a phone. */
function Maker({ world }: { world: WorldView }) {
  return (
    <>
      {world.authorName && (
        <Field label="Made by">
          {world.authorId ? <SubjectLink id={world.authorId} name={world.authorName} /> : world.authorName}
        </Field>
      )}

      {world.releaseStatus && <Field label="Who can find it">{releaseWords(world.releaseStatus)}</Field>}
    </>
  )
}

/**
 * The rest of the page as Modbot read it, at the top of the Overview. On a phone it starts with
 * what the left column says on a desk, less the name, which is the header's title there.
 */
function Details({ world, phone }: { world: WorldView; phone: boolean }) {
  return (
    <Panel title="Details">
      {phone && !world.name && world.readError && (
        <Note className="text-destructive">Couldn't read this world: {world.readError}</Note>
      )}

      <div className="flex flex-wrap gap-x-6 gap-y-2">
        {phone && <Maker world={world} />}

        {/* What the page said. Never a limit Modbot enforces -- exemptions raise real capacity
            above it (spec 3.1). */}
        {world.capacity !== null && (
          <Field label="Holds">
            <span className="font-mono">{world.capacity}</span> people
            {world.recommendedCapacity !== null && world.recommendedCapacity !== world.capacity && (
              <>
                , <span className="font-mono">{world.recommendedCapacity}</span> suggested
              </>
            )}
          </Field>
        )}

        {world.firstSeenAt && (
          <Field label="First seen by Modbot">
            <span className="font-mono">{formatDay(world.firstSeenAt)}</span>
          </Field>
        )}
        {world.publishedAt && (
          <Field label="Published on VRChat">
            <span className="font-mono">{formatDay(world.publishedAt)}</span>
          </Field>
        )}

        <Field label="Page last read">
          {world.lastReadAt ? (
            <>
              <Ago iso={world.lastReadAt} now={world.now} /> (<span className="font-mono">{dateTime(world.lastReadAt)}</span>)
            </>
          ) : (
            'never'
          )}
        </Field>
      </div>

      {world.description && (
        <Field label="Description">
          {/* The author's text, rendered as text. Never as HTML. */}
          <span className="whitespace-pre-wrap">{world.description}</span>
        </Field>
      )}
    </Panel>
  )
}

/** VRChat's release status, in a word a member would use. An unknown word is shown as sent. */
function releaseWords(status: string): string {
  return { public: 'Anyone (public)', private: 'Only people given the link (private)', hidden: 'Hidden' }[status] ?? status
}

/**
 * The glance: the rest of the page, the figures, the newest instances, and how busy the world has been
 * day by day. The charts were a Metrics tab of their own, under a copy of the same four figures.
 */
function Overview({ world, phone, onMore }: { world: WorldView; phone: boolean; onMore: (tab: Tab) => void }) {
  const c = world.counts

  return (
    <div className="flex flex-col">
      <Details world={world} phone={phone} />

      <StatStrip className="m-0 shrink-0">
        <Stat label="Time seen" value={minutes(c.minutesSeen)} />
        <Stat label="Visitors" value={compactNumber(c.visitors)} />
        <Stat label="Instances opened" value={compactNumber(world.instancesTotal)} note={<OpenNow world={world} />} />
        <Stat label="Last seen" value={c.lastSeenAt ? ago(c.lastSeenAt, world.now) : '—'} />
      </StatStrip>

      <Panel
        title="Newest instances"
        right={<More onClick={() => onMore('instances')}>All instances</More>}
        flush
      >
        {world.instances.length === 0 ? (
          <EmptyRow>No instances yet.</EmptyRow>
        ) : (
          <InstanceTable instances={world.instances.slice(0, 5)} showWorld={false} />
        )}
      </Panel>

      <PerDay world={world} />
    </div>
  )
}

/** How many of the world's instances are open now, under the count of all of them. */
function OpenNow({ world }: { world: WorldView }) {
  return (
    <>
      <span className="font-mono">{world.instancesOpenNow}</span> open now
    </>
  )
}

/** Every fact recorded in one of this world's instances, newest first. */
function History({ id }: { id: string }) {
  const live = useLiveVersion(useCallback((event: LiveEvent) => concernsWorld(event, id), [id]))
  const load = useCallback(() => api.audit({ world: id, limit: 50 }), [id])
  const { data, error, reload } = useLoad(load, live)

  return (
    <Panel title="What happened in this world" flush>
      {error && <EmptyRow tone="danger" onTryAgain={reload}>{error}</EmptyRow>}
      {!error && !data && <EmptyRow tone="loading" />}
      {data && <FactList entries={data.entries} empty="Nothing recorded yet." now={data.now} />}
    </Panel>
  )
}

/** Visitors and instances opened per day, over the days anything was recorded. Nothing when nothing was. */
function PerDay({ world }: { world: WorldView }) {
  const days = [...world.visitorsPerDay, ...world.instancesPerDay].map((p) => p.day).sort()
  const from = days[0]
  const to = days[days.length - 1]
  if (!(from && to)) return null

  return (
    <>
      <Panel title="Visitors per day">
        <DailyBars
          from={from}
          to={to}
          series={[{ key: 'visitors', label: 'visitors', one: 'visitor', points: world.visitorsPerDay, slot: 1 }]}
          emptyText="No visitors yet."
        />
      </Panel>
      <Panel title="Instances opened per day">
        <DailyBars
          from={from}
          to={to}
          series={[{ key: 'instances', label: 'instances opened', one: 'instance opened', points: world.instancesPerDay, slot: 4 }]}
          emptyText="No instances yet."
        />
      </Panel>
    </>
  )
}
