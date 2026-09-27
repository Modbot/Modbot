import { useEffect, useState } from 'react'
import { Bell, CalendarDays, Check, Copy, Gavel, Images, Lightbulb, Mail, MapPin, Settings, Users, type LucideIcon } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import type { CurrentUser, GroupInfo } from '@/lib/api'
import { groupCode, groupTabHref, groupTabs } from '@/lib/groupOverview'
import type { PageId } from '@/lib/nav'
import { useGroupInfo } from '@/lib/useGroupInfo'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { HeaderTabs } from './shared'

/**
 * The header for a tab whose content does not need the group itself (Posts, Instances): read once,
 * drawn when it arrives, and simply left out if it cannot be read, so the tab never waits on it.
 */
export function GroupHeaderFor({ me, pathOf, active }: { me: CurrentUser; pathOf: (id: PageId) => string; active: PageId }) {
  const { info } = useGroupInfo()
  return info ? <GroupHeader info={info} me={me} pathOf={pathOf} active={active} /> : null
}

/**
 * The top of the VRChat page, laid out the way the group's own page on vrchat.com is: the banner
 * with the icon over its edge, the name and counts, then VRChat's row of tabs. Modbot's own look
 * throughout; only the arrangement is VRChat's.
 *
 * On a phone the icon sits over the banner and the name goes under it; from `sm` up the name moves
 * beside the icon, level with its lower half, as VRChat has it.
 *
 * Every tab of the page draws it, with its own tab marked, so they read as one page. The row wraps
 * onto a second line on a narrow screen rather than hiding tabs off to the side.
 */
export function GroupHeader({
  info,
  me,
  pathOf,
  active = 'analytics-group',
}: {
  info: GroupInfo
  me: CurrentUser
  pathOf: (id: PageId) => string
  /** The tab marked as the one on screen. */
  active?: PageId
}) {
  const banner = vrchatMedia(info.bannerUrl)
  const icon = vrchatMedia(info.iconUrl)
  const code = groupCode(info.shortCode, info.discriminator)

  return (
    <Card className="overflow-hidden">
      {banner ? (
        <img
          src={banner}
          alt=""
          referrerPolicy="no-referrer"
          className="aspect-[3/1] max-h-64 w-full border-b border-b-(length:--hairline) bg-muted object-cover sm:aspect-[4/1]"
        />
      ) : (
        <div className="h-16 border-b border-b-(length:--hairline) bg-strip sm:h-20" />
      )}

      <div className="flex flex-col gap-2 px-(--panel-pad) pb-(--panel-pad) sm:flex-row sm:items-end sm:gap-4">
        {/* Cut out of the card by a ring of the card's own colour, over the banner's edge. */}
        {icon ? (
          <img
            src={icon}
            alt=""
            referrerPolicy="no-referrer"
            className="-mt-10 size-20 shrink-0 rounded-full bg-muted object-cover ring-4 ring-card sm:-mt-12 sm:size-24"
          />
        ) : (
          <div className="-mt-10 size-20 shrink-0 rounded-full bg-muted ring-4 ring-card sm:-mt-12 sm:size-24" />
        )}

        <div className="flex min-w-0 flex-1 flex-col gap-1 sm:pb-1">
          <h2
            className="font-display leading-tight break-words"
            style={{ fontSize: 'calc(var(--text-base) * 1.85)' }}
          >
            {info.name ?? <span className="font-mono text-muted-foreground">{info.id ?? 'No group'}</span>}
          </h2>

          <div
            className="flex flex-wrap items-center gap-x-4 gap-y-1 text-muted-foreground"
            style={{ fontSize: 'var(--text-small)' }}
          >
            <span className="flex items-center gap-1.5">
              <span aria-hidden className="size-[0.6em] shrink-0 rounded-full bg-ok" />
              <span className="font-mono text-foreground">{count(info.online)}</span> online
            </span>
            <span className="flex items-center gap-1.5">
              <Users aria-hidden className="size-[1.1em] shrink-0" />
              <span className="font-mono text-foreground">{count(info.members)}</span> members
            </span>
            {code && <GroupCode code={code} />}
          </div>
        </div>
      </div>

      <HeaderTabs
        label="Group"
        tabs={groupTabs(me).map((tab) => ({ ...tab, icon: TAB_ICONS[tab.id] }))}
        active={active}
        pathOf={(id) => groupTabHref(id, pathOf(id))}
        wrap
      />
    </Card>
  )
}

/**
 * The icon vrchat.com puts beside each tab — bulb, bell, calendar, pin, pictures, people,
 * envelope, gear, gavel — so a VRChat user finds a tab by its picture as they do there.
 */
const TAB_ICONS: Partial<Record<PageId, LucideIcon>> = {
  'analytics-group': Lightbulb,
  'group-posts': Bell,
  calendar: CalendarDays,
  'analytics-instances': MapPin,
  'group-gallery': Images,
  members: Users,
  'group-invites': Mail,
  'group-settings': Settings,
  'group-roles': Settings,
  bans: Gavel,
}

function count(n: number | null): string {
  return n === null ? '—' : n.toLocaleString()
}

/** `TESTIN.4698` and a button that copies it. A browser that refuses the clipboard says so. */
function GroupCode({ code }: { code: string }) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle')

  useEffect(() => {
    if (state === 'idle') return
    const timer = window.setTimeout(() => setState('idle'), 2000)
    return () => window.clearTimeout(timer)
  }, [state])

  const copy = () => {
    const write = navigator.clipboard?.writeText(code)
    if (!write) {
      setState('failed')
      return
    }
    write.then(
      () => setState('copied'),
      () => setState('failed'),
    )
  }

  return (
    <span className="flex items-center gap-1">
      <span className="font-mono text-foreground">{code}</span>
      <Button variant="ghost" size="icon-xs" aria-label="Copy group code" title="Copy group code" onClick={copy}>
        {state === 'copied' ? <Check className="text-ok" /> : <Copy />}
      </Button>
      {state === 'failed' && <span className="text-destructive">Could not copy</span>}
    </span>
  )
}
