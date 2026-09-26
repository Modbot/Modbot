import type { InstanceRow } from '@/lib/api'
import { PlatformBadges, RegionBadge } from '@/components/InstanceBadges'
import { cn } from '@/lib/utils'
import { openInstance } from '@/lib/subject'
import { vrchatMedia } from '@/lib/vrchatMedia'

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
          imageUrl={r.worldThumbnailImageUrl}
          people={r.peopleNow}
          capacity={r.worldCapacity}
          groupAccessType={r.groupAccessType}
          region={r.region}
          platforms={r.worldPlatforms}
        />
      ))}
    </div>
  )
}

/** The game's words for who may join a group instance. */
const ACCESS_IN_GAME: Record<string, string> = {
  members: 'Group',
  plus: 'Group+',
  public: 'Group Public',
}

/**
 * One instance the way the game draws it: the world's picture with its name and "25/40 - Group+"
 * on a dark band across the foot, the region's flag in one top corner and the world's platforms in
 * the other. Shared by every screen that lists
 * open instances, so they all look like the game and like each other. Opens the instance, whose
 * popup has its number.
 */
export function InstanceTile({
  instanceId,
  worldName,
  imageUrl,
  people,
  capacity,
  groupAccessType,
  region,
  platforms,
  className,
}: {
  instanceId: string
  worldName: string | null
  imageUrl: string | null
  people: number | null
  capacity: number | null
  groupAccessType: string | null
  region: string | null
  platforms: string[] | null
  className?: string
}) {
  const here = people ?? 0
  const count = capacity ? `${here}/${capacity}` : `${here}`
  const access = groupAccessType ? (ACCESS_IN_GAME[groupAccessType] ?? groupAccessType) : null
  const name = worldName ?? 'Unknown world'

  return (
    <button
      type="button"
      onClick={() => openInstance(instanceId)}
      aria-label={`${name}, ${count}${access ? `, ${access}` : ''}${region ? `, ${region.toUpperCase()}` : ''}`}
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
        <span className="block" style={{ fontSize: 'var(--text-small)' }}>
          {count}
          {access ? ` - ${access}` : null}
        </span>
      </span>
    </button>
  )
}
