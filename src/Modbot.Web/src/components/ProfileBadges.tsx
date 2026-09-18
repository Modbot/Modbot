import { useState } from 'react'
import { Languages, Monitor, Smartphone, Sparkles, Square } from 'lucide-react'
import { TrustRankBadge } from '@/components/TrustRankBadge'
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
        <Pill className="text-muted-foreground" title={platform.known ? undefined : 'Last platform, as VRChat sent it'}>
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
        <Pill className="border-transparent bg-gold/15 text-gold">
          <Sparkles className="size-3" aria-hidden />
          VRC+
        </Pill>
      )
    case 'staff':
      return <Pill className="border-transparent bg-info/15 text-info">VRChat staff</Pill>
    case 'nuisance':
      return <Pill className="border-transparent bg-warn/15 text-warn">Nuisance</Pill>
    case 'early-adopter':
      return <Pill className="text-muted-foreground">Early adopter</Pill>
    case 'language':
      return (
        <Pill className="text-muted-foreground" title={`language_${badge.code}`}>
          <Languages className="size-3" aria-hidden />
          {badge.name}
        </Pill>
      )
  }
}

function Pill({ className, title, children }: { className?: string; title?: string; children: React.ReactNode }) {
  return (
    <span
      className={cn(
        'inline-flex shrink-0 items-center gap-1 rounded-full border px-1.5 py-0 font-medium whitespace-nowrap',
        className,
      )}
      style={{ fontSize: '0.6875rem', borderWidth: 'var(--hairline)' }}
      title={title}
    >
      {children}
    </span>
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
        className="self-start text-muted-foreground hover:text-foreground hover:underline"
        style={{ fontSize: 'var(--text-small)' }}
      >
        {open ? 'Hide tags' : moreTagsLabel(rest.length)}
      </button>

      {open && (
        <div className="flex flex-wrap gap-1">
          {rest.map((tag) => (
            <span
              key={tag}
              className="rounded-full border px-2 py-0.5 font-mono text-muted-foreground"
              style={{ borderWidth: 'var(--hairline)', fontSize: '0.6875rem' }}
            >
              {tag}
            </span>
          ))}
        </div>
      )}
    </div>
  )
}
