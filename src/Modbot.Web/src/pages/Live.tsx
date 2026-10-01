import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { DiscordPersonLink, SourceBadge, SubjectLink } from '@/components/facts'
import { Avatar } from '@/components/discord/DiscordMemberParts'
import { InstanceHeader, InstanceTile } from '@/components/InstanceCards'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import { PanelGrid } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Card, CardHeader, CardTitle } from '@/components/ui/card'
import { api, ApiError, type LivePerson, type LiveInstance, type LiveTally, type LiveView, type LiveVoiceChannel } from '@/lib/api'
import { PRESENCE_KINDS, INSTANCE_KINDS, VOICE_GAP_MS, VOICE_KINDS, stateWord, type LiveEvent, type LiveState } from '@/lib/liveStream'
import { ago, timeOfDay } from '@/lib/format'
import { arrivedWithin, LIT_MS, NEW_MS, pinned, tallyCounts } from '@/lib/livePeople'
import { DOT, type Tone } from '@/lib/status'
import { throttle } from '@/lib/throttle'
import { useLiveStream } from '@/lib/useLiveStream'
import { PageMessage } from '@/pages/analytics/shared'
import { Marks } from '@/components/ListParts'
import { cn } from '@/lib/utils'

/**
 * How often the page asks again on its own while it is on screen. The head counts come from
 * the server's syncs rather than from events, so the stream alone would not move them.
 */
const REFRESH_MS = 30_000

/** Older than two missed reads, the age turns to the warning colour. */
const STALE_MS = 2 * REFRESH_MS

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
 * Redraws when the live stream says somebody joined or left, an instance opened or closed, or
 * somebody joined, moved or left Discord voice, and asks again every half minute on its own while
 * the tab is visible. The stream itself stops while the tab is hidden, so a tab left open in the
 * background costs the server nothing.
 */
export function Live() {
  const [data, setData] = useState<LiveView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const settle = useRef<number | undefined>(undefined)

  // When this page last heard from the server, and a clock that moves the age on every second.
  // Both are this browser's own times, so a wrong system clock cannot make the age wrong.
  const [updatedAt, setUpdatedAt] = useState<number | null>(null)
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    const tick = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(tick)
  }, [])

  const load = useCallback(() => {
    return api
      .live()
      .then((view) => {
        setData(view)
        setError(null)
        setUpdatedAt(Date.now())
      })
      .catch((e: unknown) => {
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this.'
            : 'Could not load live instances.',
        )
      })
  }, [])

  const voiceChanged = useMemo(() => throttle(load, VOICE_GAP_MS), [load])

  const stream = useLiveStream(
    useCallback(
      (event: LiveEvent) => {
        if (VOICE_KINDS.has(event.kind)) {
          voiceChanged()
          return
        }

        if (!PRESENCE_KINDS.has(event.kind) && !INSTANCE_KINDS.has(event.kind)) return
        window.clearTimeout(settle.current)
        settle.current = window.setTimeout(load, SETTLE_MS)
      },
      [load, voiceChanged],
    ),
  )

  useEffect(() => {
    let timer: number | undefined

    const follow = (returning: boolean) => {
      window.clearInterval(timer)
      timer = undefined

      if (document.visibilityState !== 'visible') return

      if (returning) load()
      timer = window.setInterval(load, REFRESH_MS)
    }

    // Read once whatever the tab's state: a Live tab opened behind another one used to sit on
    // "Loading…" until it was looked at. Only the repeating read waits for the tab to be seen.
    load()
    follow(false)
    const onVisibility = () => follow(true)
    document.addEventListener('visibilitychange', onVisibility)

    return () => {
      window.clearInterval(timer)
      window.clearTimeout(settle.current)
      voiceChanged.cancel()
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [load, voiceChanged])

  if (!data) return <PageMessage tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</PageMessage>

  // The server's time now: when it answered, moved on by how long ago that was here. Arrival
  // times are the server's, so a browser whose clock is wrong still marks the right people New.
  const serverNow = Date.parse(data.generatedAt) + (updatedAt === null ? 0 : Math.max(0, now - updatedAt))

  return (
    <div className="flex flex-col gap-3">
      <div
        className="flex flex-wrap items-center gap-x-4 gap-y-1 text-muted-foreground"
        style={{ fontSize: 'var(--text-small)' }}
      >
        {data.tally && <Tally tally={data.tally} />}
        <span className="ml-auto flex items-center gap-2">
          {updatedAt !== null && (
            <span className={cn('font-mono', now - updatedAt > STALE_MS && 'text-warn')}>
              Updated {ago(new Date(updatedAt).toISOString(), new Date(Math.max(now, updatedAt)).toISOString())}
            </span>
          )}
          <span aria-hidden className={cn('size-1.5 shrink-0', DOT[STREAM_TONE[stream]])} />
          <span>{stateWord(stream)}</span>
        </span>
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
          <PanelGrid className="desk:xl:grid-cols-2">
            {data.instances.map((instance) => (
              <InstanceCard key={instance.id} instance={instance} now={serverNow} />
            ))}
          </PanelGrid>
        )}
      </Section>

      <Section title="Discord voice">
        {!data.voice || data.voice.length === 0 ? (
          <PageMessage>Nobody in voice.</PageMessage>
        ) : (
          <PanelGrid className="desk:md:grid-cols-2 desk:xl:grid-cols-3">
            {data.voice.map((channel) => (
              <VoiceCard key={channel.channelId} channel={channel} />
            ))}
          </PanelGrid>
        )}
      </Section>
    </div>
  )
}

/** "Since 8:02 PM: 212 arrivals · 4 warns · 3 kicks · 1 ban", from the oldest open instance's opening. */
function Tally({ tally }: { tally: LiveTally }) {
  return (
    <span>
      Since <span className="font-mono">{timeOfDay(tally.since)}</span>:{' '}
      {tallyCounts(tally).map(([n, words], i) => (
        <span key={i}>
          {i > 0 && ' · '}
          <span className="font-mono text-foreground">{n}</span> {words}
        </span>
      ))}
    </span>
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
                since <span className="font-mono">{timeOfDay(p.since)}</span>
              </span>
            )}
          </li>
        ))}
      </ul>
    </Card>
  )
}

function InstanceCard({ instance, now }: { instance: LiveInstance; now: number }) {
  const watched = instance.watching.length > 0
  const reporting = new Set(instance.watching.map((w) => w.userId))
  const tile = {
    instanceId: instance.id,
    worldName: instance.worldName,
    instanceName: instance.instanceName,
    number: instance.vrChatInstanceId,
    imageUrl: instance.worldImageUrl,
    people: instance.headCount,
    peopleUnsure: instance.headCountUnsure,
    capacity: instance.worldCapacity,
    groupAccessType: instance.groupAccessType,
    region: instance.region,
    platforms: instance.worldPlatforms,
  }

  return (
    <Card>
      {/* The instance as the game draws it, and beside it who is there: known only while a
          moderator's Companion App is in the instance, whose row says so. Its number is in the
          instance's popup. On a phone the tile would be a screen-wide picture above every list,
          so there the instance is one row with a small picture, and the people come sooner. A
          headset keeps the tile: it has its own sizes and no narrow screen. */}
      <div className="flex flex-col desk:sm:flex-row">
        <div className="shrink-0 p-(--panel-pad) desk:max-sm:hidden">
          <InstanceTile {...tile} className="desk:sm:w-56 headset:max-w-sm" />
        </div>
        <InstanceHeader {...tile} className="hidden desk:max-sm:flex" />

        <div className="min-w-0 flex-1 desk:sm:border-l desk:sm:border-l-(length:--hairline)">
          {watched && <People title="Here now" people={instance.people} reporting={reporting} now={now} />}

          {!watched && instance.lastWatchedAt && instance.lastSeen.length > 0 && (
            <People title={`Last seen ${timeOfDay(instance.lastWatchedAt)}`} people={instance.lastSeen} muted />
          )}
        </div>
      </div>
    </Card>
  )
}

/**
 * Who is or was in the instance: a strip naming the list, then one row a person. The moderators
 * whose Companion App is reporting the list carry the audit log's own "Companion App" tag.
 *
 * Flagged people and anybody kicked or banned before come first (lib/livePeople.ts). Somebody
 * seen walking in within the last five minutes is marked New, and for the first minute their row
 * is lit too. A redraw alone would make a new arrival look like somebody who had been there an
 * hour. Only while the list is live: a last-seen list has nobody arriving.
 */
function People({
  title,
  people,
  muted = false,
  reporting,
  now,
}: {
  title: string
  people: LivePerson[]
  muted?: boolean
  /** VRChat ids of the moderators whose Companion App is in the instance. */
  reporting?: Set<string>
  /** The server's time now, for New. Left out for a list that is not live. */
  now?: number
}) {
  return (
    <section className="flex flex-col max-sm:border-t max-sm:border-t-(length:--hairline) headset:border-t headset:border-t-(length:--hairline)">
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
          {pinned(people).map((p) => (
            <li
              key={p.userId}
              className={cn(
                'flex min-h-(--row-h) items-center gap-2 px-(--panel-pad) py-1 transition-colors duration-1000',
                arrivedWithin(p, now, LIT_MS) && 'bg-ok/10',
              )}
            >
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
                  {arrivedWithin(p, now, NEW_MS) && <Badge variant="ok">New</Badge>}
                </Marks>
              </div>
              <span className="shrink-0 whitespace-nowrap text-muted-foreground">
                {p.arrivedAt ? (
                  <>
                    arrived <span className="font-mono">{timeOfDay(p.arrivedAt)}</span>
                  </>
                ) : p.hereBefore ? (
                  <>
                    here before <span className="font-mono">{timeOfDay(p.hereBefore)}</span>
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
