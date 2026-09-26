import { useCallback, useEffect, useRef, useState } from 'react'
import { DiscordPersonLink, SourceBadge, SubjectLink } from '@/components/facts'
import { Avatar } from '@/components/discord/DiscordMemberParts'
import { InstanceTile } from '@/components/InstanceCards'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import { PanelGrid } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Card, CardHeader, CardTitle } from '@/components/ui/card'
import { api, ApiError, type LivePerson, type LiveInstance, type LiveView, type LiveVoiceChannel } from '@/lib/api'
import { PRESENCE_KINDS, INSTANCE_KINDS, stateWord, type LiveEvent, type LiveState } from '@/lib/liveStream'
import { clockTime } from '@/lib/format'
import { DOT, type Tone } from '@/lib/status'
import { useLiveStream } from '@/lib/useLiveStream'
import { PageMessage } from '@/pages/analytics/shared'
import { Marks } from '@/pages/Members'
import { cn } from '@/lib/utils'

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

  if (!data) return <PageMessage tone={error ? 'danger' : undefined}>{error ?? 'Loading…'}</PageMessage>

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

      <Section title="VRChat">
        {data.instances.length === 0 ? (
          <PageMessage>No open instances.</PageMessage>
        ) : (
          <PanelGrid className="xl:grid-cols-2">
            {data.instances.map((instance) => (
              <InstanceCard key={instance.id} instance={instance} />
            ))}
          </PanelGrid>
        )}
      </Section>

      <Section title="Discord voice">
        {!data.voice || data.voice.length === 0 ? (
          <PageMessage>Nobody in voice.</PageMessage>
        ) : (
          <PanelGrid className="md:grid-cols-2 xl:grid-cols-3">
            {data.voice.map((channel) => (
              <VoiceCard key={channel.channelId} channel={channel} />
            ))}
          </PanelGrid>
        )}
      </Section>
    </div>
  )
}

/** A heading for one platform's half of the page. */
function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="flex flex-col gap-2">
      <h2 className="flex items-center gap-2 font-label text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {title}
        <span aria-hidden className="h-(--hairline) flex-1 bg-border" />
      </h2>
      {children}
    </section>
  )
}

/** A voice channel with people in it: its name, how many, and who, longest there first. */
function VoiceCard({ channel }: { channel: LiveVoiceChannel }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="truncate">{channel.name ?? channel.channelId}</CardTitle>
        <span className="ml-auto font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {channel.people.length}
        </span>
      </CardHeader>
      <ul className="divide-y-(length:--hairline) divide-border" style={{ fontSize: 'var(--text-small)' }}>
        {channel.people.map((p) => (
          <li key={p.userId} className="flex min-h-(--row-h) items-center gap-2 px-(--panel-pad) py-1">
            <Avatar url={p.avatarUrl} className="size-6" />
            <span className="min-w-0 flex-1 truncate">
              <DiscordPersonLink id={p.userId} name={p.displayName} />
            </span>
            {p.since && (
              <span className="shrink-0 whitespace-nowrap text-muted-foreground">
                since <span className="font-mono">{clockTime(p.since)}</span>
              </span>
            )}
          </li>
        ))}
      </ul>
    </Card>
  )
}

function InstanceCard({ instance }: { instance: LiveInstance }) {
  const watched = instance.watching.length > 0
  const reporting = new Set(instance.watching.map((w) => w.userId))

  return (
    <Card>
      {/* The instance as the game draws it, and beside it who is there: known only while a
          moderator's Companion App is in the instance, whose row says so. Its number is in the
          instance's popup. */}
      <div className="flex flex-col sm:flex-row">
        <div className="shrink-0 p-(--panel-pad)">
          <InstanceTile
            instanceId={instance.id}
            worldName={instance.worldName}
            instanceName={instance.instanceName}
            number={instance.vrChatInstanceId}
            imageUrl={instance.worldImageUrl}
            people={instance.headCount}
            capacity={instance.worldCapacity}
            groupAccessType={instance.groupAccessType}
            region={instance.region}
            platforms={instance.worldPlatforms}
            className="sm:w-56"
          />
        </div>

        <div className="min-w-0 flex-1 sm:border-l sm:border-l-(length:--hairline)">
          {watched && <People title="Here now" people={instance.people} reporting={reporting} />}

          {!watched && instance.lastWatchedAt && instance.lastSeen.length > 0 && (
            <People title={`Last seen ${clockTime(instance.lastWatchedAt)}`} people={instance.lastSeen} muted />
          )}
        </div>
      </div>
    </Card>
  )
}

/**
 * Who is or was in the instance: a strip naming the list, then one row a person. The moderators
 * whose Companion App is reporting the list carry the audit log's own "Companion App" tag.
 */
function People({
  title,
  people,
  muted = false,
  reporting,
}: {
  title: string
  people: LivePerson[]
  muted?: boolean
  /** VRChat ids of the moderators whose Companion App is in the instance. */
  reporting?: Set<string>
}) {
  return (
    <section className="flex flex-col max-sm:border-t max-sm:border-t-(length:--hairline)">
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
                  {reporting?.has(p.userId) && <SourceBadge source="Companion" />}
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
                    arrived <span className="font-mono">{clockTime(p.arrivedAt)}</span>
                  </>
                ) : p.hereBefore ? (
                  <>
                    here before <span className="font-mono">{clockTime(p.hereBefore)}</span>
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
