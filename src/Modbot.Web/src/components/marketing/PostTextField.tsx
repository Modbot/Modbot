import { Fragment, useEffect, useRef, useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { ApiError } from '@/lib/api'
import { formatDayRange } from '@/lib/format'
import {
  eventListShown,
  eventRange,
  joinEventLists,
  postsApi,
  splitEventLists,
  todayIn,
  type EventRangeKind,
  type PostEventChoice,
} from '@/lib/posts'

type Panel = 'event' | 'whats-on' | null
type DaysPick = EventRangeKind | 'dates'

const PICKS: { value: DaysPick; label: string }[] = [
  { value: 'week', label: 'This week' },
  { value: 'month', label: 'Next month' },
  { value: 'days', label: 'Next 30 days' },
  { value: 'dates', label: 'Pick dates' },
]

/**
 * A post's text, with the events inserted into it (posts design §5.3).
 *
 * The text the post stores holds marks around each event list and each time, so every site can be
 * sent its own kind of time and a later step can find the list again. Those marks are not for a
 * moderator to read, so they are never drawn: each list is a card, one with Remove, and the writer's
 * own words are the boxes around it. A list's event names are not editable here; Remove it and add
 * it again, or write around it. `splitEventLists` and `joinEventLists` turn the stored text into
 * these pieces and back, so the post's text is always the one source.
 */
export function PostTextField({
  value,
  onChange,
  timeZone,
  canAddEvents,
}: {
  value: string
  onChange: (text: string) => void
  /** The post's zone: What's on counts its days in it. */
  timeZone: string
  canAddEvents: boolean
}) {
  const parts = splitEventLists(value)
  const [panel, setPanel] = useState<Panel>(null)

  // Where the writer last had the cursor, so an event goes in there and not at the end.
  const caret = useRef<{ at: number; pos: number } | null>(null)
  const remember = (at: number, box: HTMLTextAreaElement) => {
    caret.current = { at, pos: box.selectionStart }
  }

  const setText = (at: number, text: string) => {
    const texts = [...parts.texts]
    texts[at] = text
    onChange(joinEventLists({ texts, lists: parts.lists }))
  }

  const remove = (at: number) => {
    const texts = [...parts.texts]
    const lists = [...parts.lists]
    const [before, after] = texts.splice(at, 2)
    texts.splice(at, 0, before && after ? `${before}\n\n${after}` : before || after)
    lists.splice(at, 1)
    onChange(joinEventLists({ texts, lists }))
  }

  const insert = (list: string) => {
    const texts = [...parts.texts]
    const lists = [...parts.lists]
    const last = texts.length - 1
    const at = caret.current && caret.current.at <= last ? caret.current.at : last
    const pos = caret.current && caret.current.at <= last ? Math.min(caret.current.pos, texts[at].length) : texts[at].length

    texts.splice(at, 1, texts[at].slice(0, pos).replace(/\n+$/, ''), texts[at].slice(pos).replace(/^\n+/, ''))
    lists.splice(at, 0, list)
    caret.current = null
    onChange(joinEventLists({ texts, lists }))
    setPanel(null)
  }

  const last = parts.texts.length - 1

  return (
    <div className="flex flex-col gap-1.5" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">Text</span>
      {parts.texts.map((text, i) => (
        <Fragment key={i}>
          <Textarea
            rows={parts.lists.length === 0 ? 6 : i === 0 || i === last ? 3 : 1}
            aria-label={i === 0 ? 'Text' : `Text ${i + 1}`}
            value={text}
            onChange={(e) => {
              remember(i, e.target)
              setText(i, e.target.value)
            }}
            onSelect={(e) => remember(i, e.currentTarget)}
            onBlur={(e) => remember(i, e.currentTarget)}
          />
          {i < parts.lists.length && <EventCard list={parts.lists[i]} onRemove={() => remove(i)} />}
        </Fragment>
      ))}

      {canAddEvents && (
        <div className="flex flex-wrap gap-2">
          <Button size="xs" variant="outline" aria-pressed={panel === 'event'} onClick={() => setPanel(panel === 'event' ? null : 'event')}>
            Add event
          </Button>
          <Button size="xs" variant="outline" aria-pressed={panel === 'whats-on'} onClick={() => setPanel(panel === 'whats-on' ? null : 'whats-on')}>
            What&apos;s on
          </Button>
        </div>
      )}

      {panel === 'event' && <EventPanel onInsert={insert} onClose={() => setPanel(null)} />}
      {panel === 'whats-on' && <WhatsOnPanel timeZone={timeZone} onInsert={insert} onClose={() => setPanel(null)} />}
    </div>
  )
}

/** One inserted list, as a reader of VRChat or Bluesky sees it, with the way to take it out. */
function EventCard({ list, onRemove }: { list: string; onRemove: () => void }) {
  const shown = eventListShown(list)
  const days = shown.from && shown.to ? formatDayRange(`${shown.from}T12:00:00`, `${shown.to}T12:00:00`) : null
  const label = shown.kind === 'whats-on' ? 'What’s on' : 'Event'

  return (
    <div className="flex flex-col gap-1 rounded-sm border border-(length:--hairline) border-input bg-muted/30 px-2.5 py-1.5">
      <div className="flex items-center justify-between gap-2">
        <span className="min-w-0 font-label text-muted-foreground [overflow-wrap:anywhere]">{days ? `${label}: ${days}` : label}</span>
        <Button size="xs" variant="outline" aria-label={`Remove ${label}`} onClick={onRemove}>
          Remove
        </Button>
      </div>
      <p className="whitespace-pre-wrap [overflow-wrap:anywhere]" style={{ fontSize: 'var(--text-base)' }}>
        {shown.words}
      </p>
    </div>
  )
}

function said(e: unknown): string {
  return e instanceof ApiError ? e.message : 'Could not reach the Modbot server.'
}

/** An event's next date in its own zone, for the list to pick from. */
function choiceLabel(e: PostEventChoice): string {
  let when: string
  try {
    when = new Date(e.startsAt).toLocaleString(undefined, {
      timeZone: e.timeZone,
      weekday: 'short',
      month: 'short',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
    })
  } catch {
    when = new Date(e.startsAt).toLocaleString()
  }

  return `${e.title} - ${when}`
}

function PanelFrame({ children }: { children: ReactNode }) {
  return <div className="flex flex-col gap-2 rounded-sm border border-(length:--hairline) border-input bg-muted/20 p-2.5">{children}</div>
}

function EventPanel({ onInsert, onClose }: { onInsert: (list: string) => void; onClose: () => void }) {
  const [events, setEvents] = useState<PostEventChoice[] | null>(null)
  const [picked, setPicked] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Asked once, when the panel opens.
  useEffect(() => {
    let current = true
    postsApi
      .events()
      .then((found) => {
        if (!current) return
        setEvents(found)
        setPicked(found[0]?.id ?? '')
      })
      .catch((e: unknown) => {
        if (!current) return
        setEvents([])
        setError(said(e))
      })
    return () => {
      current = false
    }
  }, [])

  const add = async () => {
    setBusy(true)
    setError(null)
    try {
      const { text } = await postsApi.eventText({ eventId: picked })
      onInsert(text)
    } catch (e) {
      setError(said(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <PanelFrame>
      {events === null ? (
        <span className="text-muted-foreground">Loading…</span>
      ) : events.length === 0 ? (
        !error && <span className="text-muted-foreground">No published events.</span>
      ) : (
        <Select value={picked} onChange={setPicked} aria-label="Event">
          {events.map((e) => (
            <option key={e.id} value={e.id}>
              {choiceLabel(e)}
            </option>
          ))}
        </Select>
      )}
      {error && <span className="text-destructive">{error}</span>}
      <div className="flex gap-2">
        <Button size="xs" disabled={busy || !picked} onClick={() => void add()}>
          Add
        </Button>
        <Button size="xs" variant="outline" onClick={onClose}>
          Cancel
        </Button>
      </div>
    </PanelFrame>
  )
}

function WhatsOnPanel({ timeZone, onInsert, onClose }: { timeZone: string; onInsert: (list: string) => void; onClose: () => void }) {
  const [pick, setPick] = useState<DaysPick>('week')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const add = async () => {
    setBusy(true)
    setError(null)
    try {
      const range = pick === 'dates' ? { from, to } : eventRange(pick, todayIn(new Date(), timeZone))
      const { text } = await postsApi.eventText({ ...range, timeZone })
      onInsert(text)
    } catch (e) {
      setError(said(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <PanelFrame>
      <Select value={pick} onChange={(v) => setPick(v as DaysPick)} aria-label="Days">
        {PICKS.map((p) => (
          <option key={p.value} value={p.value}>
            {p.label}
          </option>
        ))}
      </Select>
      {pick === 'dates' && (
        <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
          <Input type="date" aria-label="First day" value={from} onChange={(e) => setFrom(e.target.value)} />
          <Input type="date" aria-label="Last day" value={to} onChange={(e) => setTo(e.target.value)} />
        </div>
      )}
      {error && <span className="text-destructive">{error}</span>}
      <div className="flex gap-2">
        <Button size="xs" disabled={busy || (pick === 'dates' && (!from || !to))} onClick={() => void add()}>
          Add
        </Button>
        <Button size="xs" variant="outline" onClick={onClose}>
          Cancel
        </Button>
      </div>
    </PanelFrame>
  )
}
