import type { InstanceRow } from '@/lib/api'
import { PlatformBadges, RegionBadge } from '@/components/InstanceBadges'
import { cn } from '@/lib/utils'
import { openInstance } from '@/lib/subject'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { instanceCardText, instanceNumber } from '@/lib/instanceName'
import { HeadCount } from '@/components/HeadCount'

/**
 * Open instances as the game's own instance list shows them: the world's picture, and over its
 * foot the world's name and "25/40 - Group+".
 *
 * A moderator matches what is open against what they see in VRChat, so the card copies the game
 * rather than the website. The card opens the instance: who is in it, and how many over time.
 */
export function InstanceCards({ instances }: { instances: InstanceRow[] }) {
  return (
    <div className="grid grid-cols-[repeat(auto-fill,minmax(11rem,1fr))] gap-3 p-3">
      {instances.map((r) => (
        <InstanceTile
          key={r.id}
          instanceId={r.id}
          worldName={r.worldName}
          instanceName={r.instanceName}
          number={r.vrChatInstanceId}
          imageUrl={r.worldThumbnailImageUrl}
          people={r.peopleNow}
          peopleUnsure={r.peopleNowUnsure}
          capacity={r.worldCapacity}
          groupAccessType={r.groupAccessType}
          region={r.region}
          platforms={r.worldPlatforms}
        />
      ))}
    </div>
  )
}

/**
 * One instance the way the game draws it: the world's picture with its name and "25/40 - Group+"
 * on a dark band across the foot, the region's flag in one top corner and the world's platforms in
 * the other. Shared by every screen that lists
 * open instances, so they all look like the game and like each other. Opens the instance, whose
 * popup has its number. An instance opened with a name shows it in quotes under the world's, with
 * the number in the tooltip.
 */
export function InstanceTile({
  instanceId,
  worldName,
  instanceName,
  number,
  imageUrl,
  people,
  peopleUnsure = false,
  capacity,
  groupAccessType,
  region,
  platforms,
  className,
}: {
  instanceId: string
  worldName: string | null
  instanceName?: string | null
  number?: string | null
  imageUrl: string | null
  people: number | null
  /** The count came from VRChat's `n_users`: shown as "80?". */
  peopleUnsure?: boolean
  capacity: number | null
  groupAccessType: string | null
  region: string | null
  platforms: string[] | null
  className?: string
}) {
  const { here, unsure, access, name, named, tileLabel } = instanceCardText({
    worldName,
    instanceName,
    number,
    people,
    peopleUnsure,
    capacity,
    groupAccessType,
    region,
  })

  return (
    <button
      type="button"
      onClick={() => openInstance(instanceId)}
      title={named && number ? instanceNumber(number) : undefined}
      aria-label={tileLabel}
      className={cn(
        'group relative block aspect-[4/3] w-full overflow-hidden rounded-md bg-muted text-left focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring',
        className,
      )}
    >
      {imageUrl && (
        <img
          src={vrchatMedia(imageUrl)}
          alt=""
          loading="lazy"
          className="absolute inset-0 size-full object-cover transition-transform duration-200 group-hover:scale-105"
        />
      )}
      {region && (
        <span
          className="absolute top-1.5 left-1.5 rounded-sm bg-black/70 px-1.5 py-0.5 text-white"
          style={{ fontSize: 'var(--text-tiny)' }}
        >
          <RegionBadge region={region} />
        </span>
      )}
      <span className="absolute top-1.5 right-1.5">
        <PlatformBadges platforms={platforms} />
      </span>
      {/* Over a picture, so white on a dark band whatever the theme: the game's own look. */}
      <span className="absolute inset-x-0 bottom-0 bg-black/70 px-2 py-1.5 text-center leading-tight text-white">
        <span className="block truncate font-semibold">{name}</span>
        {named && <span className="block truncate">{named}</span>}
        <span className="block" style={{ fontSize: 'var(--text-small)' }}>
          <HeadCount count={here} unsure={unsure} />
          {capacity ? `/${capacity}` : null}
          {access ? ` - ${access}` : null}
        </span>
      </span>
    </button>
  )
}

/**
 * One instance as a single row, for a phone: a small square of the world's picture, then the
 * world's name and "25/40" on one line and the access type, region, number and platforms on the
 * next. The same facts as the tile, with the picture shrunk so the people in the instance come
 * first. Opens the instance, as the tile does.
 */
export function InstanceHeader({
  instanceId,
  worldName,
  instanceName,
  number,
  imageUrl,
  people,
  peopleUnsure = false,
  capacity,
  groupAccessType,
  region,
  platforms,
  className,
}: Parameters<typeof InstanceTile>[0]) {
  const { here, unsure, access, name, tag, named, headerLabel } = instanceCardText({
    worldName,
    instanceName,
    number,
    people,
    peopleUnsure,
    capacity,
    groupAccessType,
    region,
  })

  const facts = [
    access && <span key="access" className="shrink-0">{access}</span>,
    region && <RegionBadge key="region" region={region} />,
    tag && <span key="tag" className="min-w-0 truncate">{tag}</span>,
  ].filter(Boolean)

  return (
    <button
      type="button"
      onClick={() => openInstance(instanceId)}
      title={named && number ? instanceNumber(number) : undefined}
      aria-label={headerLabel}
      className={cn(
        'flex w-full items-center gap-3 p-(--panel-pad) text-left hover:bg-muted/40 focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring',
        className,
      )}
    >
      <span className="size-12 shrink-0 overflow-hidden rounded-md bg-muted">
        {imageUrl && <img src={vrchatMedia(imageUrl)} alt="" loading="lazy" className="size-full object-cover" />}
      </span>
      <span className="flex min-w-0 flex-1 flex-col gap-1 leading-tight">
        <span className="flex items-baseline gap-2">
          <span className="min-w-0 flex-1 truncate font-semibold">{name}</span>
          <span className="shrink-0 font-mono">
            <HeadCount count={here} unsure={unsure} />
            {capacity ? `/${capacity}` : null}
          </span>
        </span>
        <span className="flex min-w-0 items-center gap-1.5 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {facts.flatMap((fact, i) => (i > 0 ? [<span key={`dot${i}`} aria-hidden>·</span>, fact] : [fact]))}
          <span className="ml-auto shrink-0 pl-1">
            <PlatformBadges platforms={platforms} />
          </span>
        </span>
      </span>
    </button>
  )
}
