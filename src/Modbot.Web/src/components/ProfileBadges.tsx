import { useState } from 'react'
import { ChevronRight, Languages, Monitor, Smartphone, Sparkles, Square } from 'lucide-react'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import { Badge } from '@/components/ui/badge'
import { trustRank } from '@/lib/trustRank'
import { cn } from '@/lib/utils'
import { moreTagsLabel, parseTags, platformOf, type TagBadge } from '@/lib/vrchatTags'

/**
 * The badge row under a person's name: trust rank, VRC+, the platform they last used, staff or
 * nuisance, and their languages -- every tag on the profile that has a meaning a moderator would
 * read at a glance, as a word and a colour rather than as `system_supporter`.
 *
 * The same shape as the trust rank badge: the colour is a second channel and the word carries the
 * identity, so nothing depends on telling gold from amber. The VRC+ badge is gold text and a spark,
 * not VRChat's own logo, which is theirs.
 *
 * Staff and Nuisance are also trust ranks the server computes (they override the ladder), so when
 * the rank already says one of those the tag badge is not drawn twice.
 */
export function ProfileBadges({
  tags,
  lastPlatform,
  rank,
  className,
}: {
  tags: readonly string[] | null | undefined
  lastPlatform: string | null | undefined
  rank: unknown
  className?: string
}) {
  const parsed = parseTags(tags)
  const platform = platformOf(lastPlatform)
  const known = trustRank(rank)

  const badges = parsed.badges.filter(
    (b) => !((b.kind === 'staff' && known === 'VRChatTeam') || (b.kind === 'nuisance' && known === 'Nuisance')),
  )

  if (!known && !platform && badges.length === 0) return null

  return (
    <div className={cn('flex flex-wrap items-center gap-1', className)}>
      <TrustRankBadge rank={rank} />
      {badges.map((b) => (
        <TagPill key={b.kind === 'language' ? `language:${b.code}` : b.kind} badge={b} />
      ))}
      {platform && (
        <Pill title={platform.known ? undefined : 'Last platform, as VRChat sent it'}>
          {platform.icon === 'pc' ? (
            <Monitor className="size-3" aria-hidden />
          ) : platform.icon === 'phone' ? (
            <Smartphone className="size-3" aria-hidden />
          ) : (
            <Square className="size-3" aria-hidden />
          )}
          <span className={platform.known ? undefined : 'font-mono'}>{platform.word}</span>
        </Pill>
      )}
    </div>
  )
}

function TagPill({ badge }: { badge: TagBadge }) {
  switch (badge.kind) {
    case 'vrcplus':
      return (
        <Pill variant="gold">
          <Sparkles className="size-3" aria-hidden />
          VRC+
        </Pill>
      )
    case 'staff':
      return <Pill variant="info">VRChat staff</Pill>
    case 'nuisance':
      return <Pill variant="warn">Nuisance</Pill>
    case 'early-adopter':
      return <Pill>Early adopter</Pill>
    case 'language':
      return (
        <Pill title={`language_${badge.code}`}>
          <Languages className="size-3" aria-hidden />
          {badge.name}
        </Pill>
      )
  }
}

function Pill({
  variant = 'outline',
  className,
  title,
  children,
}: {
  variant?: 'outline' | 'warn' | 'info' | 'gold'
  className?: string
  title?: string
  children: React.ReactNode
}) {
  return (
    <Badge variant={variant} className={className} title={title}>
      {children}
    </Badge>
  )
}

/**
 * The tags that have no badge, behind a control that says how many there are. Shown as the raw
 * tag in monospace, because a tag this build has no word for is only honest as itself.
 */
export function OtherTags({ tags, className }: { tags: readonly string[] | null | undefined; className?: string }) {
  const [open, setOpen] = useState(false)
  const rest = parseTags(tags).rest

  if (rest.length === 0) return null

  return (
    <div className={cn('flex flex-col gap-1', className)}>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className="flex items-center gap-1 self-start rounded-sm text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring"
        style={{ fontSize: 'var(--text-small)' }}
      >
        <ChevronRight
          className={cn('size-3.5 shrink-0 transition-transform motion-reduce:transition-none', open && 'rotate-90')}
          aria-hidden
        />
        {open ? 'Hide tags' : moreTagsLabel(rest.length)}
      </button>

      {open && (
        <div className="flex flex-wrap gap-1">
          {rest.map((tag) => (
            <Badge key={tag} variant="outline" className="font-mono font-normal">
              {tag}
            </Badge>
          ))}
        </div>
      )}
    </div>
  )
}
