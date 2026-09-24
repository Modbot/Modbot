import { ProfileBadges } from '@/components/ProfileBadges'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'

/**
 * Who a VRChat person is, the way their own profile page shows it: the banner across the top,
 * the picture over its bottom edge, then the name, pronouns and the badge row, and the group
 * they represent under that.
 *
 * One block for the person popup's left column and the case file's snapshot, so the same person
 * looks the same in both. `pictureUrl` is the picture the server chose as the best it has; nothing
 * here falls back to another field, because the server already did. The represented group is a
 * row and not a link: it is whichever group the person chose to show, not one Modbot knows about.
 */
export function ProfileHeader({
  bannerUrl,
  pictureUrl,
  name,
  id,
  pronouns,
  tags,
  lastPlatform,
  rank,
  representedGroup,
  marks,
  className,
}: {
  bannerUrl: string | null | undefined
  pictureUrl: string | null | undefined
  name: string | null | undefined
  /** Shown in monospace when there is no name. Never parsed (spec 3.1.1). */
  id: string | null | undefined
  pronouns: string | null | undefined
  tags: readonly string[] | null | undefined
  lastPlatform: string | null | undefined
  rank: unknown
  representedGroup: { groupId?: string | null; name?: string | null; iconUrl?: string | null } | null | undefined
  /** Anything else that belongs beside the name, such as the 18+ mark. */
  marks?: React.ReactNode
  className?: string
}) {
  const banner = vrchatMedia(bannerUrl)
  const picture = vrchatMedia(pictureUrl)
  const groupIcon = vrchatMedia(representedGroup?.iconUrl)

  return (
    <div className={cn('flex flex-col', className)}>
      <div className="aspect-[3/1] max-h-48 w-full overflow-hidden bg-muted">
        {banner && <img src={banner} alt="" className="size-full object-cover" referrerPolicy="no-referrer" />}
      </div>

      <div className="-mt-8 ml-3">
        {picture ? (
          <img
            src={picture}
            alt=""
            className="size-16 rounded-full bg-muted object-cover ring-4 ring-background"
            referrerPolicy="no-referrer"
          />
        ) : (
          <div className="size-16 rounded-full bg-muted ring-4 ring-background" />
        )}
      </div>

      <div className="mt-1.5 flex min-w-0 flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="font-medium break-words" style={{ fontSize: 'var(--text-base)' }}>
            {name ?? <span className="font-mono">{id}</span>}
          </span>
          {pronouns && <span className="text-muted-foreground">{pronouns}</span>}
          {marks}
        </div>

        <ProfileBadges tags={tags} lastPlatform={lastPlatform} rank={rank} />

        {representedGroup?.name && (
          <div className="flex items-center gap-1.5" title={representedGroup.groupId ?? undefined}>
            {groupIcon ? (
              <img src={groupIcon} alt="" className="size-5 shrink-0 rounded-full bg-muted object-cover" referrerPolicy="no-referrer" />
            ) : (
              <span className="size-5 shrink-0 rounded-full bg-muted" />
            )}
            <span className="truncate">{representedGroup.name}</span>
          </div>
        )}
      </div>
    </div>
  )
}
