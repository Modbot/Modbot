import { useCallback, useEffect, useRef, useState } from 'react'
import { Volume2 } from 'lucide-react'
import { Avatar } from '@/components/discord/DiscordMemberParts'
import { DiscordPersonLink } from '@/components/facts'
import { Card } from '@/components/ui/card'
import { api, type LiveVoiceChannel } from '@/lib/api'
import { VOICE_GAP_MS, VOICE_KINDS, type LiveEvent } from '@/lib/liveStream'
import { throttle, type Throttle } from '@/lib/throttle'
import { useLiveStream } from '@/lib/useLiveStream'
import { Section } from './shared'

/** How often the list is read again on its own while the page is on screen: as often as Live reads it. */
const REFRESH_MS = 30_000

/**
 * Who is in voice right now, under the Discord page's header, drawn the way Discord's channel
 * list draws a voice channel: the speaker and the channel's name, then each person under it,
 * indented, with their picture. Only channels with somebody in them, in the server's own order;
 * with nobody in voice there is nothing to draw.
 *
 * Read from Live's own answer, which the server builds from the member rows the bot keeps, so it
 * asks nothing of Discord. Read again when the live stream says somebody joined, moved or left
 * voice -- at once, then at most every two seconds while a busy server keeps changing -- and every
 * half minute on its own in case an event was missed, and at once when the tab comes back into
 * view. A tab in the background reads nothing.
 */
export function ServerVoice() {
  const voice = useVoiceNow()

  if (!voice || voice.length === 0) return null

  return (
    <Section title="In voice">
      <Card>
        <ul className="flex flex-col py-1" style={{ fontSize: 'var(--text-small)' }}>
          {voice.map((channel) => (
            <VoiceChannel key={channel.channelId} channel={channel} />
          ))}
        </ul>
      </Card>
    </Section>
  )
}

function VoiceChannel({ channel }: { channel: LiveVoiceChannel }) {
  return (
    <li>
      <div className="flex min-h-(--row-h) items-center gap-2 px-(--panel-pad) py-1 text-muted-foreground">
        <Volume2 aria-hidden className="size-[1.1em] shrink-0" />
        <span className="min-w-0 flex-1 truncate font-medium text-foreground">
          {channel.name ?? <span className="font-mono">{channel.channelId}</span>}
        </span>
        <span className="shrink-0 font-mono">{channel.people.length}</span>
      </div>
      <ul>
        {channel.people.map((p) => (
          <li key={p.userId} className="flex items-center gap-2 py-1 pr-(--panel-pad) pl-[calc(var(--panel-pad)+1.1em+0.5rem)]">
            <Avatar url={p.avatarUrl} className="size-6" />
            <span className="min-w-0 flex-1 truncate">
              <DiscordPersonLink id={p.userId} name={p.displayName} />
            </span>
          </li>
        ))}
      </ul>
    </li>
  )
}

/** The voice part of Live's answer, kept current while the tab is on screen. Null until the first read. */
function useVoiceNow(): LiveVoiceChannel[] | null {
  const [voice, setVoice] = useState<LiveVoiceChannel[] | null>(null)
  const changed = useRef<Throttle | null>(null)

  useLiveStream(
    useCallback((event: LiveEvent) => {
      if (VOICE_KINDS.has(event.kind)) changed.current?.()
    }, []),
  )

  useEffect(() => {
    let cancelled = false
    let timer: number | undefined

    // A failed read keeps the list it had: a missed half minute is not worth an error on the page.
    const load = () => {
      api
        .live()
        .then((view) => {
          if (!cancelled) setVoice(view.voice ?? [])
        })
        .catch(() => undefined)
    }

    const redraw = throttle(load, VOICE_GAP_MS)
    changed.current = redraw

    const follow = (returning: boolean) => {
      window.clearInterval(timer)
      timer = undefined

      if (document.visibilityState !== 'visible') return

      if (returning) load()
      timer = window.setInterval(load, REFRESH_MS)
    }

    load()
    follow(false)
    const onVisibility = () => follow(true)
    document.addEventListener('visibilitychange', onVisibility)

    return () => {
      cancelled = true
      window.clearInterval(timer)
      redraw.cancel()
      changed.current = null
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [])

  return voice
}
