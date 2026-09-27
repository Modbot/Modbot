import { useEffect, useState } from 'react'
import { Check, Copy, ExternalLink, Users } from 'lucide-react'
import { WorldLink } from '@/components/facts'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { api, ApiError, type CurrentUser, type GroupInfo } from '@/lib/api'
import { calendarApi } from '@/lib/calendar'
import { whenRange } from '@/lib/format'
import { groupCode, groupTabs, isWebLink, languageName, linkLabel, nextEvent, type NextEvent } from '@/lib/groupOverview'
import type { PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { followLink } from '@/lib/router'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { PageMessage } from './shared'

/**
 * The top of the VRChat analytics page, laid out the way the group's own page on vrchat.com is:
 * the banner with the icon over its edge, the name and counts, a row of tabs, then the overview
 * cards. Modbot's own look throughout; only the arrangement is VRChat's.
 *
 * Everything about the group comes from `/api/analytics/group/info`, which reads what the
 * group-info sync already keeps, so opening the page asks VRChat nothing. The upcoming event is
 * Modbot's own calendar.
 */
export function GroupOverview({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const [info, setInfo] = useState<GroupInfo | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    api
      .groupInfo()
      .then((i) => {
        if (!cancelled) setInfo(i)
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof ApiError ? e.message : 'Could not load the group.')
      })

    return () => {
      cancelled = true
    }
  }, [])

  if (error) return <PageMessage tone="danger">{error}</PageMessage>
  if (!info) return <PageMessage>Loading…</PageMessage>

  return (
    <>
      <GroupHeader info={info} me={me} pathOf={pathOf} />

      <PanelGrid className="grid-cols-1">
        {can(me, 'ViewCalendar') && <UpcomingEvent me={me} pathOf={pathOf} />}

        <PanelGrid className="md:grid-cols-2">
          <Languages codes={info.languages} />
          <Links urls={info.links} />
        </PanelGrid>

        <About description={info.description} rules={info.rules} />
      </PanelGrid>
    </>
  )
}

/**
 * The banner, the icon over its lower edge, the name and one line of counts, then the tabs.
 *
 * On a phone the icon sits over the banner and the name goes under it; from `sm` up the name moves
 * beside the icon, level with its lower half, as VRChat has it.
 */
function GroupHeader({ info, me, pathOf }: { info: GroupInfo; me: CurrentUser; pathOf: (id: PageId) => string }) {
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

      <GroupTabs me={me} pathOf={pathOf} />
    </Card>
  )
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

/**
 * VRChat's row of group tabs, as links to the Modbot pages that show each part. A page this person
 * may not open is left out, as the sidebar leaves it out.
 */
function GroupTabs({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const tabs = groupTabs(me)

  return (
    <nav aria-label="Group" className="border-t border-t-(length:--hairline)">
      {/* Scrolls sideways on a narrow screen rather than wrapping, like the Tabs component. */}
      <div className="flex items-stretch overflow-x-auto px-1 [scrollbar-width:thin]">
        {tabs.map((tab) => {
          const active = tab.id === 'analytics-group'
          const href = pathOf(tab.id)

          return (
            <a
              key={tab.id}
              href={href}
              onClick={followLink(href)}
              aria-current={active ? 'page' : undefined}
              className={cn(
                'relative flex shrink-0 items-center px-3 font-medium whitespace-nowrap transition-colors focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring',
                active ? 'text-foreground' : 'text-muted-foreground hover:text-foreground',
              )}
              style={{ fontSize: 'var(--text-base)', minHeight: 'var(--control-h)' }}
            >
              {tab.label}
              {active && <span aria-hidden className="absolute inset-x-0 bottom-0 h-[calc(var(--hairline)*2)] bg-primary" />}
            </a>
          )
        })}
      </div>
    </nav>
  )
}

/** The next event on Modbot's calendar, or a way to make one. */
function UpcomingEvent({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const [next, setNext] = useState<NextEvent | null | undefined>(undefined)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let cancelled = false

    // From a day back, so an event under way is still found, to the furthest the calendar answers.
    const from = new Date(Date.now() - 86_400_000)
    const to = new Date(from.getTime() + 61 * 86_400_000)

    calendarApi
      .view(from, to)
      .then((view) => {
        if (!cancelled) setNext(nextEvent(view.events, view.now))
      })
      .catch(() => {
        if (!cancelled) setFailed(true)
      })

    return () => {
      cancelled = true
    }
  }, [])

  const calendar = pathOf('calendar')

  return (
    <Card>
      <CardHeader>
        <CardTitle>Upcoming event</CardTitle>
      </CardHeader>

      {failed ? (
        <EmptyRow tone="danger">Could not load the calendar.</EmptyRow>
      ) : next === undefined ? (
        <EmptyRow>Loading…</EmptyRow>
      ) : next === null ? (
        <div className="flex flex-wrap items-center justify-between gap-x-3 pr-(--panel-pad)">
          <EmptyRow>No upcoming events</EmptyRow>
          {can(me, 'ManageCalendar') && (
            <Button size="sm" asChild>
              <a href={`${calendar}?new=1`} onClick={followLink(`${calendar}?new=1`)}>
                Create event
              </a>
            </Button>
          )}
        </div>
      ) : (
        <EventRow next={next} href={`${calendar}?event=${encodeURIComponent(next.event.id)}`} />
      )}
    </Card>
  )
}

function EventRow({ next, href }: { next: NextEvent; href: string }) {
  const { event } = next
  const picture = vrchatMedia(event.imageUrl ?? event.worldThumbnailUrl)

  return (
    <div className="flex gap-3 p-(--panel-pad)">
      {picture && (
        <img
          src={picture}
          alt=""
          referrerPolicy="no-referrer"
          className="aspect-video w-28 shrink-0 self-start border border-(length:--hairline) bg-muted object-cover sm:w-40"
        />
      )}

      <div className="flex min-w-0 flex-col gap-1">
        <a href={href} onClick={followLink(href)} className="font-medium break-words hover:underline">
          {event.title}
        </a>
        <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {whenRange(next.startsAt, next.endsAt)}
        </span>
        {event.worldId && (
          <span style={{ fontSize: 'var(--text-small)' }}>
            <WorldLink id={event.worldId} name={event.worldName} unnamed="id" />
          </span>
        )}
      </div>
    </div>
  )
}

function Languages({ codes }: { codes: string[] }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Languages</CardTitle>
      </CardHeader>
      {codes.length === 0 ? (
        <EmptyRow>None added</EmptyRow>
      ) : (
        <CardContent className="flex flex-wrap gap-1.5">
          {codes.map((code) => (
            <Badge key={code} variant="secondary" title={code}>
              {languageName(code)}
            </Badge>
          ))}
        </CardContent>
      )}
    </Card>
  )
}

function Links({ urls }: { urls: string[] }) {
  const links = urls.filter(isWebLink)

  return (
    <Card>
      <CardHeader>
        <CardTitle>Links</CardTitle>
      </CardHeader>
      {links.length === 0 ? (
        <EmptyRow>None added</EmptyRow>
      ) : (
        <ul className="divide-y-(--hairline) divide-border">
          {links.map((url) => (
            <li key={url}>
              <a
                href={url}
                target="_blank"
                rel="noopener noreferrer"
                title={url}
                className="flex min-h-(--row-h) items-center gap-2 px-(--panel-pad) text-link hover:underline"
              >
                <ExternalLink aria-hidden className="size-[1em] shrink-0" />
                <span className="truncate">{linkLabel(url)}</span>
              </a>
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}

function About({ description, rules }: { description: string | null; rules: string | null }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>About this group</CardTitle>
      </CardHeader>
      {!description && !rules ? (
        <EmptyRow>None added</EmptyRow>
      ) : (
        <CardContent className="flex flex-col gap-3">
          {description && <p className="break-words whitespace-pre-wrap">{description}</p>}
          {rules && (
            <div className="flex flex-col gap-1">
              <h3 className="font-label">Rules</h3>
              <p className="break-words whitespace-pre-wrap">{rules}</p>
            </div>
          )}
        </CardContent>
      )}
    </Card>
  )
}
