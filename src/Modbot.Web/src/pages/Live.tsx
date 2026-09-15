import { useEffect, useState } from 'react'
import { SubjectLink, WorldLink } from '@/components/facts'
import { Card, CardContent } from '@/components/ui/card'
import { api, ApiError, type LivePerson, type LiveRoom, type LiveView } from '@/lib/api'
import { access } from '@/lib/format'
import { openInstance, openWorld } from '@/lib/subject'
import { PageMessage } from '@/pages/analytics/shared'

/** How often the page asks again while it is on screen. */
const REFRESH_MS = 5000

/**
 * Live -- the group's open instances right now, and who is in each.
 *
 * Every open group room is listed whether or not anybody's client is in it: the room and its head
 * count come from VRChat through the server's own syncs. Only the list of people needs a
 * moderator watching, because VRChat's instance API does not say who is inside.
 *
 * Asks every five seconds while the tab is visible and stops while it is hidden, so a tab left
 * open in the background costs the server nothing.
 */
export function Live() {
  const [data, setData] = useState<LiveView | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    let timer: number | undefined

    const load = () => {
      api
        .live()
        .then((view) => {
          if (cancelled) return
          setData(view)
          setError(null)
        })
        .catch((e: unknown) => {
          if (cancelled) return
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to see this.'
              : 'Could not load live instances.',
          )
        })
    }

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
      cancelled = true
      window.clearInterval(timer)
      document.removeEventListener('visibilitychange', follow)
    }
  }, [])

  if (!data) return <PageMessage>{error ?? 'Loading…'}</PageMessage>

  return (
    <div className="flex flex-col gap-4">
      {error && (
        <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {error}
        </p>
      )}

      {data.rooms.length === 0 ? (
        <PageMessage>No open instances.</PageMessage>
      ) : (
        <div className="grid gap-4 xl:grid-cols-2">
          {data.rooms.map((room) => (
            <RoomCard key={room.id} room={room} />
          ))}
        </div>
      )}
    </div>
  )
}

function RoomCard({ room }: { room: LiveRoom }) {
  const where = [access(room.groupAccessType), room.region?.toUpperCase()].filter(Boolean).join(' · ')
  const watched = room.watching.length > 0

  return (
    <Card className="gap-0 py-0">
      <CardContent className="flex flex-col gap-3 p-4">
        <div className="flex items-start gap-3">
          {room.worldImageUrl ? (
            <button
              type="button"
              onClick={() => openWorld(room.worldId)}
              className="shrink-0 rounded-md focus-visible:outline-2 focus-visible:outline-ring"
            >
              <img src={room.worldImageUrl} alt="" loading="lazy" className="aspect-[4/3] w-24 rounded-md object-cover" />
            </button>
          ) : null}

          <div className="min-w-0 flex-1">
            <div className="truncate font-medium">
              <WorldLink id={room.worldId} name={room.worldName} unnamed="id" />
            </div>
            <div className="flex flex-wrap items-center gap-x-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              <button
                type="button"
                onClick={() => openInstance(room.id)}
                className="rounded-md font-mono font-medium text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-ring"
              >
                {room.vrChatInstanceId ?? 'Instance'}
              </button>
              {where && <span>{where}</span>}
            </div>
          </div>

          <div className="shrink-0 text-right">
            <div className="font-mono text-2xl font-medium tabular-nums">{room.headCount ?? '—'}</div>
            <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {room.headCount === 1 ? 'person' : 'people'}
            </div>
          </div>
        </div>

        <div className="flex flex-wrap items-baseline gap-x-2" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Watching</span>
          {watched ? (
            room.watching.map((w, i) => (
              <span key={w.userId}>
                <SubjectLink id={w.userId} name={w.displayName} />
                {i < room.watching.length - 1 ? ',' : ''}
              </span>
            ))
          ) : (
            <span>Nobody watching</span>
          )}
        </div>

        {watched && <People title="Here now" people={room.people} />}

        {!watched && room.lastWatchedAt && room.lastSeen.length > 0 && (
          <People title={`Last seen ${time(room.lastWatchedAt)}`} people={room.lastSeen} muted />
        )}
      </CardContent>
    </Card>
  )
}

function People({ title, people, muted = false }: { title: string; people: LivePerson[]; muted?: boolean }) {
  return (
    <section className="flex flex-col gap-1">
      <div className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
        {title} · {people.length}
      </div>
      {people.length > 0 && (
        <ul className={muted ? 'text-muted-foreground' : undefined} style={{ fontSize: 'var(--text-small)' }}>
          {people.map((p) => (
            <li
              key={p.userId}
              className="flex flex-wrap items-baseline gap-x-2 border-t py-1"
              style={{ borderTopWidth: 'var(--hairline)' }}
            >
              <SubjectLink id={p.userId} name={p.displayName} />
              {p.standing !== 'Ordinary' && <Standing standing={p.standing} />}
              {p.flags.map((flag) => (
                <span key={flag} className="text-destructive">
                  {flag}
                </span>
              ))}
              <span className="ml-auto whitespace-nowrap text-muted-foreground tabular-nums">
                {p.arrivedAt ? `arrived ${time(p.arrivedAt)}` : p.hereBefore ? `here before ${time(p.hereBefore)}` : ''}
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function Standing({ standing }: { standing: string }) {
  return (
    <span
      className={
        standing === 'Flagged'
          ? 'rounded-md bg-destructive/10 px-1.5 font-medium text-destructive'
          : 'rounded-md bg-secondary px-1.5 text-muted-foreground'
      }
      style={{ fontSize: 'var(--text-tiny, 11px)' }}
    >
      {standing}
    </span>
  )
}

function time(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
}
