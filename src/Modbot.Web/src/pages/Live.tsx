import { useCallback, useEffect, useRef, useState } from 'react'
import { SubjectLink, WorldLink } from '@/components/facts'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import { PanelGrid } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Card, CardHeader, CardTitle } from '@/components/ui/card'
import { api, ApiError, type LivePerson, type LiveInstance, type LiveView } from '@/lib/api'
import { access } from '@/lib/format'
import { instanceNumber } from '@/lib/instanceName'
import { PRESENCE_KINDS, INSTANCE_KINDS, stateWord, type LiveEvent, type LiveState } from '@/lib/liveStream'
import { DOT, type Tone } from '@/lib/status'
import { openInstance, openWorld } from '@/lib/subject'
import { useLiveStream } from '@/lib/useLiveStream'
import { PageMessage, Stat } from '@/pages/analytics/shared'
import { Marks } from '@/pages/Members'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'

/**
 * How often the page asks again on its own while it is on screen. The head counts come from
 * the server's syncs rather than from events, so the stream alone would not move them.
 */
const REFRESH_MS = 30_000

/** A burst of joins is one redraw, not one per join. */
const SETTLE_MS = 300

/** The square before the stream's state: a warning while events are not being pushed, bad once it has given up. */
const STREAM_TONE: Record<LiveState, Tone> = {
  live: 'ok',
  polling: 'warn',
  connecting: 'warn',
  stopped: 'bad',
  off: 'muted',
}

/**
 * Live -- the group's open instances right now, and who is in each.
 *
 * Every open group instance is listed whether or not anybody's client is in it: the instance and its head
 * count come from VRChat through the server's own syncs. Only the list of people needs a
 * moderator watching, because VRChat's instance API does not say who is inside.
 *
 * Redraws when the live stream says somebody joined or left or an instance opened or closed, and
 * asks again every half minute on its own while the tab is visible. The stream itself stops
 * while the tab is hidden, so a tab left open in the background costs the server nothing.
 */
export function Live() {
  const [data, setData] = useState<LiveView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const settle = useRef<number | undefined>(undefined)

  const load = useCallback(() => {
    api
      .live()
      .then((view) => {
        setData(view)
        setError(null)
      })
      .catch((e: unknown) => {
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this.'
            : 'Could not load live instances.',
        )
      })
  }, [])

  const stream = useLiveStream(
    useCallback(
      (event: LiveEvent) => {
        if (!PRESENCE_KINDS.has(event.kind) && !INSTANCE_KINDS.has(event.kind)) return
        window.clearTimeout(settle.current)
        settle.current = window.setTimeout(load, SETTLE_MS)
      },
      [load],
    ),
  )

  useEffect(() => {
    let timer: number | undefined

    const follow = () => {
      window.clearInterval(timer)
      timer = undefined

      if (document.visibilityState !== 'visible') return

      load()
      timer = window.setInterval(load, REFRESH_MS)
    }

    follow()
    document.addEventListener('visibilitychange', follow)

    return () => {
      window.clearInterval(timer)
      window.clearTimeout(settle.current)
      document.removeEventListener('visibilitychange', follow)
    }
  }, [load])

  if (!data) return <PageMessage>{error ?? 'Loading…'}</PageMessage>

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center justify-end gap-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        <span aria-hidden className={cn('size-1.5 shrink-0', DOT[STREAM_TONE[stream]])} />
        <span>{stateWord(stream)}</span>
      </div>

      {error && (
        <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {error}
        </p>
      )}

      {data.instances.length === 0 ? (
        <PageMessage>No open instances.</PageMessage>
      ) : (
        <PanelGrid className="xl:grid-cols-2">
          {data.instances.map((instance) => (
            <InstanceCard key={instance.id} instance={instance} />
          ))}
        </PanelGrid>
      )}
    </div>
  )
}

function InstanceCard({ instance }: { instance: LiveInstance }) {
  const where = [access(instance.groupAccessType), instance.region?.toUpperCase()].filter(Boolean).join(' · ')
  const watched = instance.watching.length > 0

  return (
    <Card>
      <PanelGrid className="m-0 grid-cols-[minmax(0,1fr)_auto]">
        <div className="flex min-w-0 items-start gap-3 p-(--panel-pad)">
          {instance.worldImageUrl ? (
            <button
              type="button"
              onClick={() => openWorld(instance.worldId)}
              className="shrink-0 rounded-sm focus-visible:outline-2 focus-visible:outline-ring"
            >
              <img src={vrchatMedia(instance.worldImageUrl)} alt="" loading="lazy" className="aspect-[4/3] w-24 rounded-sm object-cover" />
            </button>
          ) : null}

          <div className="min-w-0 flex-1">
            <div className="truncate font-medium">
              <WorldLink id={instance.worldId} name={instance.worldName} unnamed="id" />
            </div>
            <div className="flex flex-wrap items-center gap-x-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              <button
                type="button"
                onClick={() => openInstance(instance.id)}
                className="rounded-sm font-mono font-medium text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-ring"
              >
                {instanceNumber(instance.vrChatInstanceId)}
              </button>
              {where && <span>{where}</span>}
            </div>
          </div>
        </div>

        <Stat
          label={instance.headCount === 1 ? 'person' : 'people'}
          value={String(instance.headCount ?? '—')}
        />
      </PanelGrid>

      <div className="flex flex-wrap items-baseline gap-x-2 p-(--panel-pad)" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Watching</span>
        {watched ? (
          instance.watching.map((w, i) => (
            <span key={w.userId}>
              <SubjectLink id={w.userId} name={w.displayName} />
              {i < instance.watching.length - 1 ? ',' : ''}
            </span>
          ))
        ) : (
          <span>Nobody watching</span>
        )}
      </div>

      {watched && <People title="Here now" people={instance.people} />}

      {!watched && instance.lastWatchedAt && instance.lastSeen.length > 0 && (
        <People title={`Last seen ${time(instance.lastWatchedAt)}`} people={instance.lastSeen} muted />
      )}
    </Card>
  )
}

/** Who is or was in the instance: a strip naming the list, then one row a person, to the panel's edges. */
function People({ title, people, muted = false }: { title: string; people: LivePerson[]; muted?: boolean }) {
  return (
    <section className="flex flex-col border-t border-t-(length:--hairline)">
      <CardHeader className={cn(people.length === 0 && 'border-b-0')}>
        <CardTitle>{title}</CardTitle>
        <span className="ml-auto font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {people.length}
        </span>
      </CardHeader>
      {people.length > 0 && (
        <ul
          className={cn('divide-y-(length:--hairline) divide-border', muted && 'text-muted-foreground')}
          style={{ fontSize: 'var(--text-small)' }}
        >
          {people.map((p) => (
            <li key={p.userId} className="flex min-h-(--row-h) items-center gap-2 px-(--panel-pad) py-1">
              {/* The name and its marks as the member lists draw them: on a phone the name keeps
                  its line and truncates, and a mark that does not fit is hidden, so the time stays
                  on the line and every row is one height. */}
              <div className="flex min-w-0 flex-1 flex-wrap items-center gap-1.5 max-md:flex-nowrap">
                <SubjectLink id={p.userId} name={p.displayName} className="max-md:max-w-full max-md:shrink-0" />
                <Marks>
                  <TrustRankBadge rank={p.trustRank} />
                  {p.standing !== 'Ordinary' && <Standing standing={p.standing} />}
                  {p.flags.map((flag) => (
                    <span key={flag} className="whitespace-nowrap text-destructive">
                      {flag}
                    </span>
                  ))}
                </Marks>
              </div>
              <span className="shrink-0 whitespace-nowrap text-muted-foreground">
                {p.arrivedAt ? (
                  <>
                    arrived <span className="font-mono">{time(p.arrivedAt)}</span>
                  </>
                ) : p.hereBefore ? (
                  <>
                    here before <span className="font-mono">{time(p.hereBefore)}</span>
                  </>
                ) : (
                  ''
                )}
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function Standing({ standing }: { standing: string }) {
  return <Badge variant={standing === 'Flagged' ? 'destructive' : 'secondary'}>{standing}</Badge>
}

function time(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
}
