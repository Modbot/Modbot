import { useEffect, useState } from 'react'
import { ExternalLink } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Card, CardAction, CardHeader, CardTitle } from '@/components/ui/card'
import { api } from '@/lib/api'
import { dateTime } from '@/lib/format'
import { changesTwitch } from '@/lib/liveRules'
import { liveFor } from '@/lib/twitch'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { useLoad } from '@/lib/useLoad'

/** How often "live for 12 min" moves on while the card is on screen. */
const AGE_MS = 30_000

const loadTwitchLive = () => api.twitchLive()

/**
 * Live on Twitch: the channel's stream while it is live -- its title, category, how long, the
 * viewer count, and the calendar event it was for -- with an Open link to the channel (Twitch
 * design, step 1 and 3). Not drawn at all while the channel is not live, the poll is off or Twitch
 * is not set up, and not drawn when the read fails, since a missing card says nothing wrong.
 *
 * The page that holds it must only draw it for somebody with See live instances: the read refuses
 * everyone else. It redraws when the live stream carries a `modbot.twitch.` fact, so no page polls.
 * The viewer count is Twitch's public one.
 */
export function TwitchLiveCard() {
  const version = useLiveVersion(changesTwitch)
  const { data } = useLoad(loadTwitchLive, version)
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), AGE_MS)
    return () => window.clearInterval(timer)
  }, [])

  const stream = data?.on ? data.stream : null
  if (!stream) return null

  return (
    <section className="flex flex-col gap-2" aria-label="Twitch">
      <Card>
        <CardHeader>
          <CardTitle className="truncate">Live on Twitch</CardTitle>
          <CardAction className="flex items-center gap-2">
            <Badge variant="ok">Live</Badge>
            {stream.link && (
              <a
                href={stream.link}
                target="_blank"
                rel="noreferrer"
                className="inline-flex items-center gap-1 text-link underline-offset-4 hover:underline"
                style={{ fontSize: 'var(--text-small)' }}
              >
                Open
                <ExternalLink aria-hidden className="size-3.5" />
              </a>
            )}
          </CardAction>
        </CardHeader>
        <div className="flex flex-col gap-1 px-(--panel-pad) pb-(--panel-pad)" style={{ fontSize: 'var(--text-small)' }}>
          <div className="font-medium">{stream.title || data?.channelName || 'Untitled'}</div>
          <div className="flex flex-wrap gap-x-3 gap-y-1 text-muted-foreground">
            {stream.category && <span>{stream.category}</span>}
            <span title={dateTime(stream.startedAt)}>
              Live for <span className="font-mono text-foreground">{liveFor(stream.startedAt, now)}</span>
            </span>
            <span>
              <span className="font-mono text-foreground">{stream.viewers.toLocaleString()}</span>{' '}
              {stream.viewers === 1 ? 'viewer' : 'viewers'}
            </span>
            {stream.eventTitle && <span>{stream.eventTitle}</span>}
          </div>
        </div>
      </Card>
    </section>
  )
}
