import { useEffect, useState } from 'react'
import { DiscordPersonLink, SubjectLink } from '@/components/facts'
import { EmptyRow } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Card, CardHeader, CardTitle } from '@/components/ui/card'
import { api, type CurrentUser, type LiveInstance, type NowChange, type NowLook, type ReviewView } from '@/lib/api'
import { moderationApi, type ModerationFlag } from '@/lib/autoMod'
import { writeChips } from '@/lib/filters'
import { ago, clockTime, formatDay, headCountText, plural } from '@/lib/format'
import { instanceName } from '@/lib/instanceName'
import { changesFlags } from '@/lib/liveRules'
import { INSTANCE_KINDS, PRESENCE_KINDS, REVIEW_KINDS, type LiveEvent } from '@/lib/liveStream'
import { mayOpen, type PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { followLink } from '@/lib/router'
import { DOT, statusLine, TONE, type StatusRowId } from '@/lib/status'
import { openInstance } from '@/lib/subject'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { useLoad } from '@/lib/useLoad'
import { useStatusRows } from '@/lib/useStatusRows'
import { cn } from '@/lib/utils'

/** How many of each queue's newest rows the page lists. The rest are one click away. */
const FLAG_ROWS = 5
const REVIEW_ROWS = 3

/** How often the instances are read again while the page is on screen, as Live does. */
const INSTANCES_MS = 30_000

/**
 * How often the page tells the server it is still on screen. Well inside the server's fifteen
 * minutes, so looking at Now for an hour is one look rather than four.
 */
const LOOK_MS = 5 * 60_000

const isReviewEvent = (event: LiveEvent) => REVIEW_KINDS.has(event.kind)
const isInstanceEvent = (event: LiveEvent) => PRESENCE_KINDS.has(event.kind) || INSTANCE_KINDS.has(event.kind)

const loadFlags = () => moderationApi.flags('open')
const loadReviews = () => api.reviews('open')
const loadLive = () => api.live()

/**
 * Now -- the front page (UX review 2026-09-25, finding 3 and big idea 1). It answers "what needs
 * me" and "what changed", and lists only things somebody can act on: the numbers stay on the
 * analytics pages.
 *
 * Every part is read from the endpoint its own page uses, under that page's permission, so a
 * part this person cannot open is simply not drawn. Join requests are a link without a number:
 * VRChat is the only place they are read, and whether that is worth a request here is still open.
 */
export function Now({
  me,
  onOpenSubject,
  onGo,
  onOpenHealth,
}: {
  me: CurrentUser
  onOpenSubject: (id: string) => void
  onGo: (page: PageId) => void
  /** Opens the Health page, at one part when the line names one. */
  onOpenHealth: (section: StatusRowId | null) => void
}) {
  const seesFlags = mayOpen(me, 'flags')
  const seesReviews = mayOpen(me, 'reviews')
  const seesRequests = mayOpen(me, 'requests')
  const seesLive = mayOpen(me, 'live')
  const seesHealth = can(me, 'ViewOperationalLog')

  const flags = useLoad(seesFlags ? loadFlags : null, useLiveVersion(changesFlags))
  const reviews = useLoad(seesReviews ? loadReviews : null, useLiveVersion(isReviewEvent))

  const tick = useWhileVisible(INSTANCES_MS)
  const live = useLoad(seesLive ? loadLive : null, useLiveVersion(isInstanceEvent) + tick)

  const look = useLook()

  // The server's clock where one has answered, for the ages; the browser's only until then.
  const now = look?.now ?? reviews.data?.now ?? new Date().toISOString()

  return (
    <div className="flex flex-col gap-3">
      {seesHealth && <HealthLine onOpen={onOpenHealth} />}

      {(seesFlags || seesReviews || seesRequests) && (
        <Decisions
          flags={seesFlags ? flags : null}
          reviews={seesReviews ? reviews : null}
          seesRequests={seesRequests}
          now={now}
          onOpenSubject={onOpenSubject}
          onGo={onGo}
        />
      )}

      {seesLive && <Instances live={live} onOpenSubject={onOpenSubject} />}

      {look?.changes && <SinceLastLooked look={look} changes={look.changes} />}
    </div>
  )
}

/** A number that goes up every `ms` while the tab is on screen, and at once when it comes back. */
function useWhileVisible(ms: number): number {
  const [tick, setTick] = useState(0)

  useEffect(() => {
    let timer: number | undefined

    const follow = () => {
      window.clearInterval(timer)
      timer = undefined
      if (document.visibilityState !== 'visible') return
      timer = window.setInterval(() => setTick((t) => t + 1), ms)
    }

    const onVisibility = () => {
      if (document.visibilityState === 'visible') setTick((t) => t + 1)
      follow()
    }

    follow()
    document.addEventListener('visibilitychange', onVisibility)

    return () => {
      window.clearInterval(timer)
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [ms])

  return tick
}

/**
 * Tells the server the page is on screen, when it opens and every few minutes while it stays, and
 * keeps what it answers. A tab in the background says nothing, so the time away is counted.
 */
function useLook(): NowLook | null {
  const [look, setLook] = useState<NowLook | null>(null)
  const tick = useWhileVisible(LOOK_MS)

  useEffect(() => {
    let cancelled = false

    api
      .lookAtNow()
      .then((next) => !cancelled && setLook(next))
      .catch(() => undefined)

    return () => {
      cancelled = true
    }
  }, [tick])

  return look
}

/** Modbot's health in one line. Opens the Health page at the part it names. */
function HealthLine({ onOpen }: { onOpen: (section: StatusRowId | null) => void }) {
  const line = statusLine(useStatusRows())

  return (
    <div className="flex justify-end">
      <button
        type="button"
        onClick={() => onOpen(line.section)}
        className="flex min-h-(--control-h) items-center gap-2 px-1 hover:underline"
        style={{ fontSize: 'var(--text-small)' }}
      >
        <span aria-hidden className={cn('size-2.5 shrink-0', DOT[line.tone])} />
        <span className={line.tone === 'ok' ? 'text-muted-foreground' : TONE[line.tone]}>{line.text}</span>
      </button>
    </div>
  )
}

type Loaded<T> = { data: T | null; error: string | null }

/** Open flags and reviews, a count each and the newest rows, and the way to join requests. */
function Decisions({
  flags,
  reviews,
  seesRequests,
  now,
  onOpenSubject,
  onGo,
}: {
  flags: Loaded<{ flags: ModerationFlag[]; open: number }> | null
  reviews: Loaded<{ reviews: ReviewView[]; openCount: number }> | null
  seesRequests: boolean
  now: string
  onOpenSubject: (id: string) => void
  onGo: (page: PageId) => void
}) {
  const flagRows = [...(flags?.data?.flags ?? [])]
    .sort((a, b) => Date.parse(b.flaggedAt) - Date.parse(a.flaggedAt))
    .slice(0, FLAG_ROWS)
  const reviewRows = [...(reviews?.data?.reviews ?? [])]
    .sort((a, b) => Date.parse(b.openedAt) - Date.parse(a.openedAt))
    .slice(0, REVIEW_ROWS)

  const loading = (flags !== null && !flags.data && !flags.error) || (reviews !== null && !reviews.data && !reviews.error)
  const failed = [flags?.error && !flags.data ? 'flags' : null, reviews?.error && !reviews.data ? 'reviews' : null].filter(
    (x): x is string => x !== null,
  )

  return (
    <Card>
      <CardHeader>
        <CardTitle>Needs a decision</CardTitle>
        <div className="ml-auto flex flex-wrap items-center gap-x-3" style={{ fontSize: 'var(--text-small)' }}>
          {flags?.data && (
            <CountLink count={flags.data.open} one="open flag" onClick={() => onGo('flags')} />
          )}
          {reviews?.data && (
            <CountLink count={reviews.data.openCount} one="review" onClick={() => onGo('reviews')} />
          )}
          {seesRequests && (
            <button type="button" onClick={() => onGo('requests')} className="text-muted-foreground hover:text-foreground hover:underline">
              Join requests
            </button>
          )}
        </div>
      </CardHeader>

      {failed.length > 0 && <EmptyRow tone="danger">Could not load the {failed.join(' or the ')}.</EmptyRow>}

      {flagRows.length + reviewRows.length > 0 ? (
        <ul className="divide-y-(length:--hairline) divide-border" style={{ fontSize: 'var(--text-small)' }}>
          {flagRows.map((flag) => (
            <li key={flag.id} className="flex min-h-(--row-h) items-center gap-2 px-(--panel-pad) py-1">
              <Badge variant="outline">Flag</Badge>
              <span className="shrink-0">
                {flag.subjectPlatform === 'vrchat' ? (
                  <SubjectLink id={flag.subjectId} name={flag.subjectName} onOpen={onOpenSubject} />
                ) : (
                  <DiscordPersonLink id={flag.subjectId} name={flag.subjectName} />
                )}
              </span>
              <button
                type="button"
                onClick={() => onGo('flags')}
                className="min-w-0 flex-1 truncate text-left text-muted-foreground hover:text-foreground"
              >
                {flag.ruleName} · {flag.picture ?? `“${flag.matched}”`}
              </button>
              <span className="shrink-0 font-mono text-muted-foreground">{ago(flag.flaggedAt, now)}</span>
            </li>
          ))}
          {reviewRows.map((review) => (
            <li key={review.id} className="flex min-h-(--row-h) items-center gap-2 px-(--panel-pad) py-1">
              <Badge variant="outline">Review</Badge>
              <button
                type="button"
                onClick={() => onGo('reviews')}
                className="min-w-0 flex-1 truncate text-left hover:underline"
              >
                {review.moderator.name ?? review.moderator.id}
                <span className="text-muted-foreground"> · {review.signalLabel}</span>
              </button>
              <span className="shrink-0 font-mono text-muted-foreground">{ago(review.openedAt, now)}</span>
            </li>
          ))}
        </ul>
      ) : (
        failed.length === 0 && <EmptyRow>{loading ? 'Loading…' : 'Nothing waiting'}</EmptyRow>
      )}
    </Card>
  )
}

function CountLink({ count, one, onClick }: { count: number; one: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn('hover:underline', count > 0 ? 'text-foreground' : 'text-muted-foreground')}
    >
      <span className="font-mono">{count}</span> {plural(count, one)}
    </button>
  )
}

/**
 * The group's open instances, the ones with flagged people in them first, and in each the flagged
 * people by name. Everybody else is on Live: this is the part of it that needs somebody.
 */
function Instances({
  live,
  onOpenSubject,
}: {
  live: Loaded<{ instances: LiveInstance[] }>
  onOpenSubject: (id: string) => void
}) {
  const flaggedIn = (instance: LiveInstance) => instance.people.filter((p) => p.standing === 'Flagged')

  const instances = [...(live.data?.instances ?? [])].sort(
    (a, b) => flaggedIn(b).length - flaggedIn(a).length || (b.headCount ?? 0) - (a.headCount ?? 0),
  )

  return (
    <Card>
      <CardHeader>
        <CardTitle>In the group's instances</CardTitle>
      </CardHeader>
      {!live.data ? (
        <EmptyRow tone={live.error ? 'danger' : undefined}>{live.error ?? 'Loading…'}</EmptyRow>
      ) : instances.length === 0 ? (
        <EmptyRow>No open instances</EmptyRow>
      ) : (
        <ul className="divide-y-(length:--hairline) divide-border" style={{ fontSize: 'var(--text-small)' }}>
          {instances.map((instance) => {
            const flagged = flaggedIn(instance)
            const watched = instance.watching.length > 0

            return (
              <li key={instance.id} className="flex flex-col px-(--panel-pad) py-1">
                <div className="flex min-h-(--row-h) items-center gap-2">
                  <button
                    type="button"
                    onClick={() => openInstance(instance.id)}
                    className="min-w-0 truncate text-left font-medium hover:underline"
                  >
                    {instanceName(instance.worldName, instance.worldId, instance.vrChatInstanceId, instance.instanceName)}
                  </button>
                  {flagged.length === 0 && (
                    <span className="shrink-0 text-muted-foreground">{watched ? 'Nobody flagged' : 'Nobody watching'}</span>
                  )}
                  {instance.headCount !== null && (
                    <span className="ml-auto shrink-0 font-mono text-muted-foreground">
                      {headCountText(instance.headCount, instance.headCountUnsure)}
                      {instance.worldCapacity ? `/${instance.worldCapacity}` : ''}
                    </span>
                  )}
                </div>
                {flagged.map((p) => (
                  <div key={p.userId} className="flex min-h-(--row-h) items-center gap-2 pl-3">
                    <Badge variant="destructive">Flagged</Badge>
                    <span className="shrink-0">
                      <SubjectLink id={p.userId} name={p.displayName} onOpen={onOpenSubject} />
                    </span>
                    <span className="min-w-0 truncate text-muted-foreground">{p.flags.join(' · ')}</span>
                  </div>
                ))}
              </li>
            )
          })}
        </ul>
      )}
    </Card>
  )
}

/** The word each kind of change is counted in. */
const CHANGE_WORDS: Record<NowChange['kind'], string> = {
  bans: 'ban',
  unbans: 'unban',
  kicks: 'kick',
  warns: 'warn',
  caseFiles: 'case file',
  notes: 'note',
  flags: 'flag',
  joinRequests: 'join request',
  joins: 'join',
  leaves: 'leave',
}

/**
 * The audit log at these fact types, from the day the counting starts. The log's date filter is by
 * the day, so it may show a few more from earlier that day than the count here.
 */
function auditAt(change: NowChange, since: string): string {
  const params = new URLSearchParams()
  writeChips(params, [
    { property: 'type', operator: 'is', values: change.types },
    { property: 'when', operator: 'after', values: [since.slice(0, 10)] },
  ])
  return `/audit?${params.toString()}`
}

/** What happened since this person last looked, one count a kind, each opening the audit log. */
function SinceLastLooked({ look, changes }: { look: NowLook; changes: NowChange[] }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>{look.lookedBefore ? 'Since you last looked' : 'In the last day'}</CardTitle>
        {look.lookedBefore && (
          <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {formatDay(look.since)} {clockTime(look.since)}
          </span>
        )}
      </CardHeader>
      {changes.length === 0 ? (
        <EmptyRow>Nothing new</EmptyRow>
      ) : (
        <div
          className="flex min-h-(--row-h) flex-wrap items-center gap-x-1 px-(--panel-pad) py-1"
          style={{ fontSize: 'var(--text-small)' }}
        >
          {changes.map((change, i) => {
            const to = auditAt(change, look.since)
            return (
              <span key={change.kind} className="whitespace-nowrap">
                {i > 0 && <span className="text-muted-foreground"> · </span>}
                <a href={to} onClick={followLink(to)} className="hover:underline">
                  <span className="font-mono">{change.count}</span> {plural(change.count, CHANGE_WORDS[change.kind])}
                </a>
              </span>
            )
          })}
        </div>
      )}
    </Card>
  )
}
