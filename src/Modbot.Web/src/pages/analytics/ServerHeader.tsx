import { useEffect, useState } from 'react'
import { CalendarDays, ChartColumn, Gavel, Users, type LucideIcon } from 'lucide-react'
import { Card } from '@/components/ui/card'
import { api, type CurrentUser, type ServerProfile } from '@/lib/api'
import { formatDay, plural } from '@/lib/format'
import type { PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import {
  boostGoal,
  boostShare,
  discordPicture,
  serverInitials,
  serverTabHref,
  serverTabs,
  type BoostGoal,
} from '@/lib/serverOverview'
import { HeaderTabs } from './shared'

/**
 * The header over a Modbot page that the server's row leads out to (Bans), drawn only when the page
 * was opened from that row (`serverTabFrom`), so the person has not left the Discord page. Read on
 * its own, gated on See members like the member list it also sits over, and left out if it cannot
 * be read, so the page below never waits on it.
 */
export function ServerPageTop({ me, tab, pathOf }: { me: CurrentUser; tab: PageId; pathOf: (id: PageId) => string }) {
  const [server, setServer] = useState<ServerProfile | null>(null)
  const allowed = can(me, 'ViewMembers')

  useEffect(() => {
    if (!allowed) return
    let cancelled = false

    api
      .discordServer()
      .then((next) => {
        if (!cancelled) setServer(next)
      })
      .catch(() => undefined)

    return () => {
      cancelled = true
    }
  }, [allowed])

  if (!server) return null

  return (
    <div className="mb-3">
      <ServerHeader server={server} me={me} pathOf={pathOf} active={tab} />
    </div>
  )
}

/**
 * The top of the Discord analytics page, laid out the way Discord's own server profile is: the
 * banner, the icon over its edge as Discord's rounded square, the name and one line of counts, the
 * boost bar, then a row of links. Modbot's own look throughout; only the arrangement is Discord's.
 *
 * Everything but the online count is what the bot already stored. The online count is the one thing
 * asked of Discord, when the page opens, and the server keeps each answer five minutes; the bot has
 * no presence intent, so it cannot count it itself. With the bot offline the count is left out.
 *
 * On a phone the icon sits over the banner and the name goes under it; from `sm` up the name moves
 * beside the icon, level with its lower half, as on the VRChat page.
 *
 * The Discord members page draws it too, with Members marked, so the two read as one Discord page,
 * and so does the Bans page opened from the row, on its Discord list with Bans marked.
 */
export function ServerHeader({
  server,
  me,
  pathOf,
  active = 'analytics-server',
}: {
  server: ServerProfile
  me: CurrentUser
  pathOf: (id: PageId) => string
  /** The link marked as the page on screen. */
  active?: PageId
}) {
  const banner = discordPicture(server.bannerUrl, 1024)
  const icon = discordPicture(server.iconUrl, 256)
  const boosts = boostGoal(server.boostCount, server.boostLevel)

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
        <ServerIcon src={icon} name={server.name} />

        <div className="flex min-w-0 flex-1 flex-col gap-1 sm:pb-1">
          <h2 className="font-display leading-tight break-words" style={{ fontSize: 'calc(var(--text-base) * 1.85)' }}>
            {server.name ?? <span className="font-mono text-muted-foreground">{server.guildId ?? 'No server'}</span>}
          </h2>

          <div
            className="flex flex-wrap items-center gap-x-4 gap-y-1 text-muted-foreground"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {server.online !== null && (
              <span className="flex items-center gap-1.5">
                <span aria-hidden className="size-[0.6em] shrink-0 rounded-full bg-ok" />
                <span className="font-mono text-foreground">{count(server.online)}</span> Online
              </span>
            )}
            <span className="flex items-center gap-1.5">
              <span aria-hidden className="size-[0.6em] shrink-0 rounded-full bg-muted-foreground" />
              <span className="font-mono text-foreground">{count(server.members)}</span> Members
            </span>
            {server.createdAt && (
              <span>
                Est. <span className="font-mono text-foreground">{formatDay(server.createdAt, true)}</span>
              </span>
            )}
          </div>
        </div>
      </div>

      {boosts && <BoostBar goal={boosts} />}

      <HeaderTabs
        label="Server"
        tabs={serverTabs(me).map((tab) => ({ ...tab, icon: TAB_ICONS[tab.id] }))}
        active={active}
        pathOf={(id) => serverTabHref(id, pathOf(id))}
      />
    </Card>
  )
}

/**
 * The icon Discord puts beside each part of a server -- calendar for Events, people for Members --
 * so a Discord user finds a link by its picture, as on the VRChat row. Discord has no Overview; the
 * chart is Server Insights', which is what that page is.
 */
const TAB_ICONS: Partial<Record<PageId, LucideIcon>> = {
  'analytics-server': ChartColumn,
  calendar: CalendarDays,
  'discord-members': Users,
  bans: Gavel,
}

function count(n: number | null): string {
  return n === null ? '—' : n.toLocaleString()
}

/**
 * Discord's rounded square, cut out of the card by a ring of the card's own colour over the
 * banner's edge. With no icon, the server's initials on a plain square, as Discord draws it.
 */
function ServerIcon({ src, name }: { src: string | null; name: string | null }) {
  const shape = '-mt-10 size-20 shrink-0 rounded-[28%] bg-muted ring-4 ring-card sm:-mt-12 sm:size-24'

  if (src) return <img src={src} alt="" referrerPolicy="no-referrer" className={`${shape} object-cover`} />

  return (
    <div aria-hidden className={`${shape} flex items-center justify-center overflow-hidden font-display text-foreground`}>
      <span style={{ fontSize: 'calc(var(--text-base) * 1.5)' }}>{serverInitials(name)}</span>
    </div>
  )
}

/** "Level 2", the bar towards the next level, and "9/14 boosts"; at level 3 a full bar and the count. */
function BoostBar({ goal }: { goal: BoostGoal }) {
  const share = boostShare(goal)
  const text = goal.goal === null ? `${goal.boosts} ${plural(goal.boosts, 'boost')}` : `${goal.boosts}/${goal.goal} boosts`

  return (
    <div
      className="flex flex-wrap items-center gap-x-3 gap-y-1.5 border-t border-t-(length:--hairline) px-(--panel-pad) py-2"
      style={{ fontSize: 'var(--text-small)' }}
    >
      <span className="shrink-0 font-medium">{goal.level === 0 ? 'No level' : `Level ${goal.level}`}</span>
      <div
        role="progressbar"
        aria-label="Boosts"
        aria-valuemin={0}
        aria-valuemax={goal.goal ?? goal.boosts}
        aria-valuenow={goal.boosts}
        aria-valuetext={text}
        className="h-2 min-w-24 flex-1 overflow-hidden rounded-full bg-muted"
      >
        <div className="h-full rounded-full bg-primary" style={{ width: `${share * 100}%` }} />
      </div>
      <span className="shrink-0 font-mono text-muted-foreground">{text}</span>
    </div>
  )
}
