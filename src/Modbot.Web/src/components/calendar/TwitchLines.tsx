import { useEffect, useState } from 'react'
import { Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { api, ApiError, type TwitchStream } from '@/lib/api'
import type { CalendarEvent } from '@/lib/calendar'
import { dateTime } from '@/lib/format'

/**
 * "Streamed on Twitch" on an event (Twitch design, step 3): each time the channel streamed while the
 * event was on, with a link to the channel. Somebody with Manage calendar can take a stream off the
 * event, or link another from the channel's recent streams; Modbot links a stream to the one event on
 * when it started and leaves it alone when events overlap, and what a person chose here it never
 * changes again. Nothing is drawn when the event has no stream and there is none to link.
 */
export function TwitchLines({ event, onChanged }: { event: CalendarEvent; onChanged: (() => void) | null }) {
  const streams = event.twitchStreams ?? []
  const [recent, setRecent] = useState<TwitchStream[]>([])
  const [choice, setChoice] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  // Only somebody who may link reads the channel's recent streams, and once per event shown.
  const mayLink = onChanged !== null
  const linked = streams.length

  useEffect(() => {
    if (!mayLink) return
    let cancelled = false

    api
      .twitchStreams()
      .then((list) => !cancelled && setRecent(list))
      .catch(() => !cancelled && setRecent([]))

    return () => {
      cancelled = true
    }
  }, [event.id, linked, mayLink])

  const linkable = recent.filter((s) => s.eventId !== event.id)

  const run = (call: () => Promise<unknown>) => {
    setBusy(true)
    setProblem(null)

    call()
      .then(() => {
        setChoice('')
        onChanged?.()
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  if (streams.length === 0 && linkable.length === 0) return null

  return (
    <div className="flex flex-col gap-1">
      {streams.map((stream) => (
        <div key={stream.id} className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span>
            <span className="text-muted-foreground">Streamed on Twitch </span>
            <span className="font-mono">{dateTime(stream.startedAt)}</span>
          </span>
          {stream.link && (
            <a className="underline" href={stream.link} target="_blank" rel="noreferrer">
              Open
            </a>
          )}
          {onChanged && (
            <Button size="sm" variant="outline" disabled={busy} onClick={() => run(() => api.linkTwitchStream(stream.id, null))}>
              Remove
            </Button>
          )}
        </div>
      ))}

      {onChanged && linkable.length > 0 && (
        <div className="flex flex-wrap items-center gap-2">
          <Select value={choice} onChange={setChoice} aria-label="Twitch stream" disabled={busy} className="max-w-full">
            <option value="">Link a Twitch stream</option>
            {linkable.map((stream) => (
              <option key={stream.id} value={stream.id}>
                {dateTime(stream.startedAt)} · {stream.title || 'Untitled'}
                {stream.eventTitle ? ` · ${stream.eventTitle}` : ''}
              </option>
            ))}
          </Select>
          <Button size="sm" variant="outline" disabled={busy || !choice} onClick={() => run(() => api.linkTwitchStream(choice, event.id))}>
            Link
          </Button>
        </div>
      )}

      <Outcome tone="problem">{problem}</Outcome>
    </div>
  )
}
